using Wandur.Core.Storage;
using Wandur.Core.Scripting;
using Wandur.Desktop.Services;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Wandur.Core.Sessions;
using Wandur.Core.Settings;
using Wandur.Core.Terminal;
using Wandur.Desktop.Security;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController : IAsyncDisposable
{
    public Wandur.Core.Discovery.WorldTheme? WorldTheme { get; private set; }
    private readonly ISettingsStore _store;
    private IMudSession? _session;
    private CancellationTokenSource? _connectionCancellation;
    private readonly AnsiTerminal _promptTerminal = new(8);
    private Regex _passwordPrompt = AutoLoginSequence.Compile(AutoLoginSequence.DefaultPasswordPrompt);
    private bool _serverPrivate;
    private bool _promptPrivate;
    private bool _manualPrivate;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _startPending;
    private bool _disposed;
    private readonly Queue<(IMudSession Session, string Text, long ScriptEpoch, ScriptEvent? Event, RoomObservation? Room)> _pending = new();
    private readonly object _pendingLock = new();
    private bool _scriptPrivacyBlocked;
    private long _scriptOutputEpoch;
    private int _pendingCharacters;
    private bool _droppedOutput;
    private readonly DispatcherTimer _outputTimer;

    public WorkspaceController(Wandur.Desktop.Terminal.ITranscriptDisplayFactory displays, ISettingsStore store, IPasswordVault passwords, IRoomMapStore maps, IScriptRuntimeFactory scriptRuntimes, IWorldScriptLibraryStore scriptLibraryStore, IWorldKnowledgeStore? knowledge = null, IAgentClientServices? agents = null)
    {
        _store = store; _knowledge = knowledge;
        Display = displays.Create(Terminal);
        Passwords = passwords;
        _maps = maps;
        ScriptLibrary = new WorldScriptLibrary(scriptRuntimes, scriptLibraryStore,
            () => IsConnected && !IsConnecting && !_disposed,
            () => IsPrivate || _login is not null,
            command => SendCoreAsync(command, fromScript: true),
            text => { Terminal.AppendLocalText(L.ScriptOutputPrefix + text + "\n"); TerminalVersion++; Changed?.Invoke(); });
        ScriptLibrary.Changed += () => Changed?.Invoke();
        InitializeAgent(agents);
        var loaded = store.Load();
        Settings = loaded.Settings;
        Display.ApplySettings(Settings);
        ThemeService.Apply(Settings);
        if (loaded.Warning is not null) Notice = loaded.Warning;
        _outputTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => { FlushOutput(); SaveMap(); ScriptLibrary.Tick(); });
        _outputTimer.Start();
        Wandur.Core.Localization.UiLanguage.Changed += RefreshLanguage;
    }

    public WorldScriptLibrary ScriptLibrary { get; }
    private ViewModels.SessionPagesViewModel? _pages;
    public ViewModels.SessionPagesViewModel Pages => _pages ??= new(ScriptLibrary);
    public ClientSettings Settings { get; private set; }
    public AnsiTerminal Terminal { get; } = new();
    public Wandur.Desktop.Terminal.ITranscriptDisplay Display { get; }
    public CommandHistory History { get; private set; } = new();
    public string WorldName { get; private set; } = L.YourNextAdventure;
    public string Endpoint { get; private set; } = L.ChooseASavedWorldOrTryTheOfflineDemo;
    public string Status { get; private set; } = L.ReadyToWander;
    public string? Notice { get; private set; }
    public bool IsConnected => _session?.IsConnected == true;
    public bool IsConnecting { get; private set; }
    public bool HasSession { get; private set; }
    public bool IsDemo => _session is DemoSession;
    public bool IsPrivate => _manualPrivate || _serverPrivate || _promptPrivate;
    public bool ManualPrivate => _manualPrivate;
    public int TerminalVersion { get; private set; }
    public int OutputVersion { get; private set; }
    public int CommandsSent { get; private set; }
    public event Action? Changed;
    public event Action<ClientSettings>? SettingsSaved;

    public void SetManualPrivate(bool enabled) { _manualPrivate = enabled; RefreshScriptState(); Changed?.Invoke(); }
    public void ShowNotice(string? notice) { Notice = notice; Changed?.Invoke(); }
    public void ClearTranscript() { Terminal.Clear(); TerminalVersion++; Changed?.Invoke(); }

    private void RefreshLanguage()
    {
        Diagnostics.RefreshLanguage();
        if (!HasSession)
        {
            WorldName = L.YourNextAdventure;
            Endpoint = L.ChooseASavedWorldOrTryTheOfflineDemo;
            Status = L.ReadyToWander;
        }
        Changed?.Invoke();
    }

    public void SaveSettings(ClientSettings settings)
    {
        _store.Save(settings);
        ApplySettings(settings);
        SettingsSaved?.Invoke(settings);
    }

    internal void ApplySettings(ClientSettings settings)
    {
        Settings = settings;
        Display.ApplySettings(settings);
        ThemeService.Apply(settings);
        TerminalVersion++;
        Changed?.Invoke();
    }

    public async Task StartAsync(ConnectionProfile? profile = null)
    {
        if (_disposed) return;
        if (_startPending || IsConnecting || IsConnected) { ShowNotice(L.DisconnectFromTheCurrentWorldBeforeStartingAnotherSession); return; }
        _startPending = true;
        IsConnecting = true;
        Changed?.Invoke();
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            await DisconnectCoreAsync();
            IMudSession session = profile is null ? new DemoSession() : new TelnetSession(profile);
            _session = session;
            HasSession = true;
            _mappingRefreshPending = false;
            _protocolBindings = profile?.GetProtocolMapping() is { } mapping ? new Wandur.Core.Protocol.ProtocolBindingEngine(mapping) : null;
            WorldTheme = profile?.Theme is { IsValid: true } theme ? theme : null;
            _connectionCancellation = new();
            var token = _connectionCancellation.Token;
            StartMapping(profile, session);
            IsConnecting = true;
            WorldName = profile?.Name ?? "The Lantern & the Rain";
            Endpoint = profile is null ? L.OFFLINEDEMOASmallWorldOnYourOwnMachine : $"{profile.Host}:{profile.Port}  /  {profile.Encoding.ToUpperInvariant()}";
            ScriptLibrary.Configure(profile is null ? "demo" : $"{profile.Host.Trim().ToLowerInvariant()}:{profile.Port}:{profile.UseTls}", WorldName);
            Agent?.Configure(profile is null ? "demo" : $"{profile.Host.Trim().ToLowerInvariant()}:{profile.Port}:{profile.UseTls}");
            Status = L.Connecting; Notice = null; CommandsSent = 0;
            _serverPrivate = _promptPrivate = _manualPrivate = false;
            History = new(); ClearTranscript(); _promptTerminal.Clear();
            Diagnostics.ClearCommand.Execute(null);
            _passwordPrompt = AutoLoginSequence.Compile(profile?.PasswordPrompt ?? AutoLoginSequence.DefaultPasswordPrompt);
            var wirePrivate = false;
            void ReceiveText(string text, bool containsPrivateText)
            {
                lock (_pendingLock)
                {
                    if (_pendingCharacters + text.Length > 524_288 || _pending.Count >= 2048) { _pending.Clear(); _pendingCharacters = 0; _droppedOutput = true; }
                    _pending.Enqueue((session, text, containsPrivateText || wirePrivate || _scriptPrivacyBlocked ? -1 : _scriptOutputEpoch, null, null)); _pendingCharacters += text.Length;
                }
            }
            if (session is TelnetSession telnet)
            {
                telnet.PrivateIntervalReceived += () =>
                {
                    _agentRunner?.CancelPending();
                    Dispatch(session, () => ResetAgentContext());
                };
                telnet.TextReceived += received => ReceiveText(received.Text, received.MayContainPrivateText);
                telnet.ProtocolMessageReceived += received => ReceiveProtocol(session, received, wirePrivate);
                telnet.GmcpLoginReceived += message => Dispatch(session, () => _ = ReceiveGmcpLoginAsync(telnet, message));
                telnet.GmcpMessageReceived += received =>
                {
                    lock (_pendingLock)
                    {
                        if (_pendingCharacters + received.Text.Length > 524_288 || _pending.Count >= 2048)
                        { _pending.Clear(); _pendingCharacters = 0; _droppedOutput = true; }
                        var epoch = received.MayContainPrivateText || wirePrivate || _scriptPrivacyBlocked ? -1 : _scriptOutputEpoch;
                        _pending.Enqueue((session, "", epoch, new("gmcp", received.Text), null));
                        _pendingCharacters += received.Text.Length;
                    }
                };
            }
            else session.Output += text => ReceiveText(text, false);
            session.StatusChanged += status => Dispatch(session, () =>
            {
                Status = status.Message;
                if (!status.Connected) { ResetAgentContext(); StopMapWalk("MapWalkDisconnected"); ScriptLibrary.Stop(); _login = null; IsConnecting = false; _serverPrivate = _promptPrivate = _manualPrivate = false; }
                RefreshScriptState(); Changed?.Invoke();
            });
            session.PrivateInputChanged += value =>
            {
                // Stamp privacy on the receiving thread, before any queued UI callback can run.
                _agentRunner?.CancelPending();
                lock (_pendingLock) { wirePrivate = value; _scriptOutputEpoch++; }
                Dispatch(session, () => { _serverPrivate = value; RefreshScriptState(); Changed?.Invoke(); });
            };
            Changed?.Invoke();
            try
            {
                var loginNotice = await PrepareLoginAsync(profile, token);
                RefreshScriptState();
                await session.ConnectAsync(token);
                if (ReferenceEquals(_session, session) && loginNotice is not null) Notice = loginNotice;
            }
            catch (OperationCanceledException) { if (ReferenceEquals(_session, session)) Status = token.IsCancellationRequested ? L.ConnectionCanceled : L.ConnectionTimedOut; }
            catch (Exception ex) { if (ReferenceEquals(_session, session)) { Status = L.CouldNotConnect; Notice = profile is null ? ex.Message : ConnectionError.Describe(ex, profile); } }
            finally
            {
                if (ReferenceEquals(_session, session)) { IsConnecting = false; FlushOutput(); RefreshScriptState(); Changed?.Invoke(); }
            }
        }
        finally { _startPending = false; IsConnecting = false; _lifecycle.Release(); Changed?.Invoke(); }
    }

    private void RefreshScriptState()
    {
        if (IsPrivate || _login is not null) { StopMapWalk("MapWalkPrivate"); if (_agentPublicText.Length > 0 || _agentProtocol.Length > 0 || _agentRunner?.IsBusy == true) ResetAgentContext(); }
        lock (_pendingLock)
        {
            var blocked = IsPrivate || _login is not null;
            if (_session is TelnetSession telnet) telnet.SetLocalPrivateInput(blocked);
            if (_scriptPrivacyBlocked != blocked)
            {
                // REPORT often predates login. Ask for a current snapshot once public play resumes.
                if (_scriptPrivacyBlocked && !blocked && _protocolBindings is not null) _mappingRefreshPending = true;
                _scriptPrivacyBlocked = blocked; _scriptOutputEpoch++;
            }
        }
        ScriptLibrary.RefreshState();
        _ = RefreshMappedProtocolSubscriptionAsync();
    }

    private void Dispatch(IMudSession session, Action action)
    {
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_session, session)) action(); });
    }

    public void FlushOutput()
    {
        List<(IMudSession Session, string Text, long ScriptEpoch, ScriptEvent? Event, RoomObservation? Room)> batch;
        List<(IMudSession Session, long Epoch, PendingProtocolDiagnostic Message)> diagnostics;
        bool dropped;
        lock (_pendingLock)
        {
            if (_pending.Count == 0 && _pendingDiagnostics.Count == 0) return;
            batch = [.. _pending]; _pending.Clear(); _pendingCharacters = 0;
            diagnostics = [.. _pendingDiagnostics]; _pendingDiagnostics.Clear(); _pendingDiagnosticBytes = 0;
            dropped = _droppedOutput; _droppedOutput = false;
        }
        if (dropped) { ResetAgentContext(); StopMapWalk("MapWalkOutputLost"); Terminal.Clear(); Notice = L.OutputArrivedTooQuicklyOlderOutputWasDiscardedTo; }
        bool changed = false;
        var scriptChunks = new List<(string Text, long Epoch, ScriptEvent? Event)>();
        foreach (var item in batch)
        {
            if (!ReferenceEquals(item.Session, _session)) continue;
            if (item.Room is { } room) { ObserveRoom(room); changed = true; continue; }
            if (item.Event is null) { Terminal.Append(item.Text); _promptTerminal.Append(item.Text); TrackMapOutput(item.Text); }
            scriptChunks.Add((item.Text, item.ScriptEpoch, item.Event)); changed = true;
        }
        if (!changed && !diagnostics.Any(item => ReferenceEquals(item.Session, _session))) return;
        TerminalVersion++;
        OutputVersion++;
        var prompt = CurrentPrompt;
        try { _promptPrivate = IsConnected && prompt.Length <= 512 && _passwordPrompt.IsMatch(prompt); }
        catch (RegexMatchTimeoutException) { _promptPrivate = true; }
        RefreshScriptState();
        if (dropped) ScriptLibrary.Stop();
        long currentEpoch;
        lock (_pendingLock) currentEpoch = _scriptOutputEpoch;
        foreach (var item in diagnostics.Where(item => ReferenceEquals(item.Session, _session)))
        {
            var payload = item.Epoch == currentEpoch && !IsPrivate && _login is null ? item.Message.Payload : null;
            Diagnostics.AppendContent(item.Message.ReceivedAt, item.Message.Option, item.Message.Content);
            if (payload is not null) _protocolBindings?.Observe(item.Message.Option, item.Message.Content, item.Message.ReceivedAt);
            FeedAgentProtocol(item.Message.Option, payload);
        }
        foreach (var chunk in scriptChunks)
        {
            if (chunk.Epoch == currentEpoch)
            {
                if (chunk.Event is null && !IsPrivate && _login is null) FeedAgentText(chunk.Text);
                if (chunk.Event is not null) ScriptLibrary.Publish(chunk.Event);
                else ScriptLibrary.Feed(chunk.Text);
            }
            else ScriptLibrary.DiscardPartialLine();
        }
        Changed?.Invoke();
        if (_session is { } session) _ = TryAutoLoginAsync(session);
    }

    public async Task<bool> SendAsync(string command)
    {
        ResetAgentContext("AgentManual");
        StopMapWalk("MapWalkManualCommand");
        _login = null; // Manual input takes over the handshake.
        FlushOutput();
        RefreshScriptState();
        var session = _session;
        if (session?.IsConnected != true) { ShowNotice(L.ConnectToAWorldBeforeSendingCommands); return false; }
        var wasPrivate = IsPrivate;
        if (!wasPrivate && await ScriptLibrary.HandleCommandAsync(command))
        {
            if (ReferenceEquals(_session, session) && !IsPrivate) History.Add(command, false);
            return true;
        }
        // A queued alias must never turn into password input or cross a reconnect.
        if (!ReferenceEquals(_session, session) || wasPrivate != IsPrivate) return false;
        return await SendCoreAsync(command);
    }

    private async Task<bool> SendCoreAsync(string command, bool fromScript = false, bool fromMapWalk = false, bool fromAgent = false, CancellationToken cancellationToken = default, Wandur.Core.Agents.AgentObservation? agentObservation = null)
    {
        // A password prompt may already be queued even though the UI timer has not flushed it.
        if (fromScript || fromMapWalk || fromAgent) FlushOutput();
        if (_agentOwnsControl && !fromAgent) return false;
        if (fromAgent && (!_agentOwnsControl || cancellationToken.IsCancellationRequested || agentObservation is null ||
            agentObservation.Revision != _agentRevision || agentObservation.Generation != _agentGeneration ||
            _session is TelnetSession { RemoteEcho: true })) return false;
        if (!fromMapWalk) StopMapWalk("MapWalkManualCommand");
        else if (_mapWalk is not { } walk || !CanContinueMapWalk(walk) || cancellationToken.IsCancellationRequested) return false;
        var session = _session;
        if (session?.IsConnected != true) { ShowNotice(L.ConnectToAWorldBeforeSendingCommands); return false; }
        if ((fromScript || fromMapWalk || fromAgent) && (IsPrivate || _login is not null)) return false;
        var isPrivate = IsPrivate;
        if (isPrivate) RememberDiagnosticSecret(command);
        TrackMapCommand(command, isPrivate);
        // Enter advances the local display even when command text is not echoed.
        // Do this before awaiting the write: a fast server response must follow this boundary.
        var localText = !isPrivate && (Settings.LocalEcho || IsDemo) ? command : "";
        _promptTerminal.Clear();
        Terminal.AppendLocalText(localText + "\n");
        TerminalVersion++;
        Changed?.Invoke();
        try
        {
            await session.SendCommandAsync(command, cancellationToken);
            if (!ReferenceEquals(_session, session)) return true;
            if (!fromScript && !fromMapWalk && !fromAgent) History.Add(command, isPrivate);
            CommandsSent++;
            FlushOutput();
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) { if (ReferenceEquals(_session, session)) ShowNotice(L.Format(L.CommandWasnTSent, ex.Message)); return false; }
    }

    public async Task DisconnectAsync()
    {
        StopMapWalk("MapWalkDisconnected");
        // Cancel before taking the lock so a connection attempt does not hold up shutdown.
        _connectionCancellation?.Cancel();
        await _lifecycle.WaitAsync();
        try { await DisconnectCoreAsync(); IsConnecting = false; Changed?.Invoke(); }
        finally { _lifecycle.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        ResetAgentContext();
        StopMapWalk("MapWalkDisconnected");
        ScriptLibrary.Stop();
        _login = null;
        FlushOutput();
        SaveMap(force: true);
        _promptTerminal.Clear();
        var previous = _session;
        var cancellation = _connectionCancellation;
        _session = null;
        _connectionCancellation = null;
        cancellation?.Cancel();
        if (previous is not null) await previous.DisposeAsync();
        _diagnosticSecrets = [];
        cancellation?.Dispose();
        _serverPrivate = _promptPrivate = _manualPrivate = false;
        Status = L.Disconnected;
        RefreshScriptState();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; Wandur.Core.Localization.UiLanguage.Changed -= RefreshLanguage; _outputTimer.Stop(); await DisconnectAsync(); _pages?.Dispose(); Agent?.Dispose(); await ScriptLibrary.DisposeAsync(); Display.Dispose(); }
}
