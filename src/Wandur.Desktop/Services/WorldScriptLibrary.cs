using System.Collections.ObjectModel;
using System.ComponentModel;
using Wandur.Core.Scripting;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Services;

public sealed class WorldScriptEntry : INotifyPropertyChanged
{
    private string _name;
    private string _source;
    private MacroDefinition? _macro;
    internal WorldScriptDefinition Saved { get; set; }
    internal WorldScriptEntry(WorldScriptDefinition definition, SessionScripts runtime)
    {
        Saved = definition; _name = definition.Name; _source = definition.Source; _macro = definition.Macro; Runtime = runtime;
        Runtime.Changed += () => PropertyChanged?.Invoke(this, new(nameof(Runtime)));
    }
    public Guid Id => Saved.Id;
    public string Name { get => _name; set { value ??= ""; if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
    public string Source { get => _source; set { value ??= ""; if (_source == value) return; _source = value; PropertyChanged?.Invoke(this, new(nameof(Source))); } }
    public MacroDefinition? Macro { get => _macro; set { if (_macro == value) return; _macro = value; PropertyChanged?.Invoke(this, new(nameof(Macro))); } }
    public bool IsMacro => Saved.Macro is not null;
    public bool IsPack => Saved.Pack is not null;
    public ScriptPackInfo? Pack => Saved.Pack;
    public bool AllowSend => Saved.AllowSend;
    public bool Enabled => Saved.Enabled;
    public bool HasUnsavedChanges => Name != Saved.Name || Source != Saved.Source || Macro != Saved.Macro;
    public SessionScripts Runtime { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyEnabled() => PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    internal void NotifyPack()
    {
        foreach (var property in new[] { nameof(IsPack), nameof(Pack), nameof(AllowSend) })
            PropertyChanged?.Invoke(this, new(property));
    }
}

/// <summary>The scripts of one session, independently enabled, all hosted by the session's one worker process.
/// Editor drafts are separate from saved, executable source.</summary>
public sealed class WorldScriptLibrary : IAsyncDisposable
{
    private readonly IWorldScriptLibraryStore _store;
    private readonly Func<bool> _canRun;
    private readonly Func<bool> _isPrivate;
    private readonly Func<string, Task<bool>> _send;
    private readonly Action<string> _echo;
    private readonly Action<string>? _report;
    private readonly HashSet<Guid> _attempted = [];
    private readonly Queue<long> _sentAt = [];
    private string? _worldKey;
    private bool _disposed;
    private bool _suspended;
    private bool _refreshing;
    public ObservableCollection<WorldScriptEntry> Items { get; } = [];
    /// <summary>The docked panels this session's scripts declare.</summary>
    public ScriptPanelHost Panels { get; } = new();
    /// <summary>The session's worker: started when the first script runs, ended with the session's world or the library.</summary>
    public SessionScriptWorker Worker { get; }
    public bool MacrosEnabled { get; private set; } = true;
    public string WorldName { get; private set; } = "";
    public string? Error { get; private set; }
    public event Action? Changed;

    /// <param name="seedState">The session's protocol state, sent to the worker before a script runs whenever it has changed.</param>
    /// <param name="report">Called with an MSDP variable a script read that the world has not sent.</param>
    public WorldScriptLibrary(IScriptRuntimeFactory factory, IWorldScriptLibraryStore store, Func<bool> canRun,
        Func<bool> isPrivate, Func<string, Task<bool>> send, Action<string> echo,
        Func<string?>? seedState = null, Action<string>? report = null)
    {
        _store = store; _canRun = canRun; _isPrivate = isPrivate; _send = send; _echo = echo; _report = report;
        Worker = new SessionScriptWorker(factory, seedState);
        Worker.Crashed += OnWorkerCrashed;
        Panels.Callback = DeliverPanelEvent;
        Items.Add(Create(new(Guid.NewGuid(), L.ScriptDefaultName, ScriptExamples.Starter)));
    }

    private sealed class EntrySourceStore(string source) : IWorldScriptStore
    {
        public string? Load(string worldKey) => source;
        public void Save(string worldKey, string value) => source = value;
    }

    private WorldScriptEntry Create(WorldScriptDefinition definition)
    {
        WorldScriptEntry? created = null;
        var runtime = new SessionScripts(Worker, definition.Id.ToString(), new EntrySourceStore(definition.Source), _canRun, _isPrivate, SendAsync, _echo,
            action => Panels.Apply(definition.Id, action),
            () => created?.Saved is { Pack: not null, AllowSend: false },
            _report);
        runtime.Configure(_worldKey ?? "", WorldName);
        runtime.Changed += OnChanged;
        var entry = created = new WorldScriptEntry(definition, runtime);
        entry.PropertyChanged += (_, args) => { if (args.PropertyName != nameof(WorldScriptEntry.Runtime)) OnChanged(); };
        return entry;
    }

    private void DeliverPanelEvent(Guid scriptId, string message)
    {
        if (_disposed || _suspended) return;
        // A callback is an ordinary script event: privacy, rate limits and the send policy all still apply.
        Items.FirstOrDefault(entry => entry.Id == scriptId)?.Runtime.Publish(new("panel", message));
    }

    private Task<bool> SendAsync(string command)
    {
        var now = Environment.TickCount64;
        while (_sentAt.TryPeek(out var first) && now - first >= 60_000) _sentAt.Dequeue();
        if (_sentAt.Count >= 200 || _sentAt.Count(t => now - t < 1000) >= 20)
            throw new InvalidOperationException(L.ScriptRateExceeded);
        _sentAt.Enqueue(now);
        return _send(command);
    }

    /// <summary>The worker process died. Its scripts are already marked failed; when the restart budget allows,
    /// the enabled ones among them run again from scratch on a fresh process, seeded from the host cache.</summary>
    private void OnWorkerCrashed(IReadOnlyList<SessionScripts> failed, bool restart)
    {
        if (_disposed) return;
        foreach (var entry in Items) if (failed.Contains(entry.Runtime)) _attempted.Remove(entry.Id);
        if (restart) RefreshState(); else Changed?.Invoke();
    }

    private void OnChanged()
    {
        // A stopped script keeps no panels; the workspace closes their docked tools on the next sync.
        foreach (var entry in Items) if (!entry.Runtime.IsRunning) Panels.RemoveScript(entry.Id);
        if (!_refreshing) Changed?.Invoke();
    }

    public void Configure(string key, string name)
    {
        if (_disposed) return;
        Stop(); _attempted.Clear(); _sentAt.Clear(); WorldName = name;
        if (_worldKey != key)
        {
            _worldKey = key;
            foreach (var entry in Items) { entry.Runtime.Changed -= OnChanged; _ = entry.Runtime.DisposeAsync(); }
            Items.Clear(); Panels.Clear(); Error = null;
            try { foreach (var definition in _store.Load(key)) Items.Add(Create(definition)); }
            catch (Exception ex) when (IsStorageError(ex)) { Error = L.Format(L.ScriptLoadFailed, ex.Message); }
        }
        Changed?.Invoke();
    }

    public WorldScriptEntry Add() => AddDefinition(new(Guid.NewGuid(), L.ScriptDefaultName, ScriptExamples.Starter));

    public WorldScriptEntry AddMacro()
    {
        var macro = new MacroDefinition(MacroKind.Trigger, "You are hungry", "eat bread");
        return AddDefinition(new(Guid.NewGuid(), L.MacroNewName, MacroCompiler.Compile(macro), Macro: macro));
    }

    private WorldScriptEntry AddDefinition(WorldScriptDefinition definition)
    {
        var entry = Create(definition);
        if (!_disposed && Items.Count < WorldScriptLibraryStore.MaximumScripts)
        {
            Items.Add(entry);
            Persist(entry.Saved);
        }
        else Error = L.ScriptLibraryTooLarge;
        Changed?.Invoke();
        return entry;
    }

    public async Task DeleteAsync(WorldScriptEntry entry)
    {
        if (_disposed || !Items.Contains(entry)) return;
        try
        {
            if (_worldKey is not null) _store.Delete(_worldKey, entry.Id);
            entry.Runtime.Changed -= OnChanged;
            await entry.Runtime.DisposeAsync();
            Items.Remove(entry); _attempted.Remove(entry.Id); Error = null;
        }
        catch (Exception ex) when (IsStorageError(ex)) { Error = L.Format(L.ScriptSaveFailed, ex.Message); }
        Changed?.Invoke();
    }

    /// <summary>Adds or refreshes the scripts a world directory supplies. Hand-written scripts are untouched.
    /// The listing is the whole pack: a supplied script it no longer names is removed, since pack scripts are
    /// read only and a regeneration may rename them.</summary>
    public void ApplyPack(IReadOnlyList<Wandur.Core.Discovery.WorldScriptListing> supplied)
    {
        if (_disposed || _worldKey is null || supplied.Count == 0) return;
        var wanted = new HashSet<Guid>(supplied.Select(listing => ScriptPackInfo.IdFor(_worldKey, listing.Id)));
        foreach (var stale in Items.Where(entry => entry.IsPack && !wanted.Contains(entry.Id)).ToArray())
        {
            try
            {
                _store.Delete(_worldKey, stale.Id);
                stale.Runtime.Changed -= OnChanged;
                stale.Runtime.Stop();
                _ = stale.Runtime.DisposeAsync();
                Items.Remove(stale); _attempted.Remove(stale.Id);
            }
            catch (Exception ex) when (IsStorageError(ex)) { Error = L.Format(L.ScriptSaveFailed, ex.Message); }
        }
        foreach (var listing in supplied)
        {
            var info = new ScriptPackInfo(listing.Id, listing.Provenance, listing.Version, listing.Description);
            var id = ScriptPackInfo.IdFor(_worldKey, listing.Id);
            var name = listing.Name.Trim();
            var existing = Items.FirstOrDefault(entry => entry.Id == id);
            if (existing is null)
            {
                // Supplied scripts arrive enabled, with sending refused until the user allows it.
                AddDefinition(new(id, name, listing.Source, Enabled: true) { Pack = info });
                continue;
            }
            // A refresh replaces the source and keeps the user's enable and send-policy choices.
            if (existing.Saved.Pack is not { } previous || previous.Version == listing.Version) continue;
            var saved = existing.Saved with { Name = name, Source = listing.Source, Pack = info };
            if (!Persist(saved)) continue;
            existing.Saved = saved;
            existing.Name = saved.Name; existing.Source = saved.Source;
            existing.Runtime.Source = saved.Source;
            existing.NotifyPack();
            existing.Runtime.Stop(); _attempted.Remove(existing.Id);
        }
        RefreshState();
    }

    /// <summary>Lifts or restores the restricted send policy of one supplied script.</summary>
    public async Task SetAllowSendAsync(WorldScriptEntry entry, bool allow)
    {
        if (_disposed || !Items.Contains(entry) || !entry.IsPack || entry.AllowSend == allow) return;
        var saved = entry.Saved with { AllowSend = allow };
        if (!Persist(saved)) return;
        entry.Saved = saved; entry.NotifyPack();
        entry.Runtime.Stop(); _attempted.Remove(entry.Id);
        await ActivateAsync(entry);
        Changed?.Invoke();
    }

    /// <summary>Copies a script into an editable hand-written entry.</summary>
    public WorldScriptEntry Duplicate(WorldScriptEntry entry)
    {
        var name = L.Format(L.ScriptDuplicateName, entry.Name);
        return AddDefinition(new(Guid.NewGuid(), name.Length > 120 ? name[..120] : name, entry.Source));
    }

    public async Task SetEnabledAsync(WorldScriptEntry entry, bool enabled, bool persist = true)
    {
        if (_disposed || !Items.Contains(entry) || entry.Enabled == enabled) return;
        var saved = entry.Saved with { Enabled = enabled };
        if (persist && !Persist(saved)) return;
        entry.Saved = saved; entry.NotifyEnabled();
        entry.Runtime.Stop(); _attempted.Remove(entry.Id);
        await ActivateAsync(entry);
        Changed?.Invoke();
    }

    public async Task SaveAsync(WorldScriptEntry entry)
    {
        // A supplied script is read-only; the user duplicates it to make changes.
        if (_disposed || !Items.Contains(entry) || entry.IsPack) return;
        WorldScriptDefinition saved;
        try { saved = entry.Saved with { Name = entry.Name.Trim(), Source = entry.Macro is { } macro ? MacroCompiler.Compile(macro) : entry.Source, Macro = entry.Macro }; }
        catch (ArgumentException error) { Error = error.Message; Changed?.Invoke(); return; }
        if (!Persist(saved)) return;
        entry.Saved = saved; entry.Name = saved.Name; entry.Source = saved.Source;
        entry.Runtime.Source = saved.Source;
        entry.Runtime.Stop(); _attempted.Remove(entry.Id);
        await ActivateAsync(entry);
        Changed?.Invoke();
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException;
    private bool Persist(WorldScriptDefinition definition)
    {
        try
        {
            if (_worldKey is null) throw new IOException(L.ScriptNeedConnection);
            _store.Upsert(_worldKey, definition); Error = null;
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        { Error = L.Format(L.ScriptSaveFailed, ex.Message); Changed?.Invoke(); return false; }
    }

    private Task ActivateAsync(WorldScriptEntry entry)
    {
        if (_disposed || _suspended || (entry.IsMacro && !MacrosEnabled) || !entry.Enabled || !Items.Contains(entry) || !_canRun() || _isPrivate() || !_attempted.Add(entry.Id)) return Task.CompletedTask;
        entry.Runtime.Source = entry.Saved.Source;
        return entry.Runtime.RunAsync();
    }

    public async Task SetMacrosEnabledAsync(bool enabled)
    {
        if (_disposed || MacrosEnabled == enabled) return;
        MacrosEnabled = enabled;
        foreach (var entry in Items.Where(e => e.IsMacro).ToArray())
        {
            entry.Runtime.Stop(); _attempted.Remove(entry.Id);
            if (enabled) await ActivateAsync(entry);
        }
        Changed?.Invoke();
    }

    public async Task ReloadAsync()
    {
        if (_disposed || _worldKey is null) return;
        IReadOnlyList<WorldScriptDefinition> definitions;
        try { definitions = _store.Load(_worldKey); }
        catch (Exception error) when (IsStorageError(error))
        { Error = L.Format(L.ScriptLoadFailed, error.Message); Changed?.Invoke(); return; }
        _refreshing = true;
        try
        {
            foreach (var entry in Items) { entry.Runtime.Changed -= OnChanged; await entry.Runtime.DisposeAsync(); }
            Items.Clear(); Panels.Clear(); _attempted.Clear(); Error = null;
            foreach (var definition in definitions) Items.Add(Create(definition));
        }
        finally { _refreshing = false; }
        RefreshState();
    }

    public void RefreshState()
    {
        if (_disposed) return;
        _refreshing = true;
        try
        {
            foreach (var entry in Items)
            {
                entry.Runtime.RefreshState();
                _ = ActivateAsync(entry);
            }
        }
        finally { _refreshing = false; }
        Changed?.Invoke();
    }
    public void Tick() { foreach (var entry in Items) entry.Runtime.Tick(); }
    /// <summary>Server text: completed lines reach every running script, once each, in the same order.</summary>
    public void Feed(string text)
    {
        if (_isPrivate() || !Items.Any(entry => entry.Runtime.IsRunning)) { Worker.DiscardPartialLine(); return; }
        Worker.Feed(text);
    }
    /// <summary>One event to every running script, and to one still loading, through the worker's single queue.</summary>
    public void Publish(ScriptEvent input)
    {
        if (_isPrivate() || !Items.Any(entry => entry.Runtime.IsRunning || entry.Runtime.IsBusy)) return;
        Worker.Publish(input);
    }
    public bool HandleShortcut(string key)
    {
        if (_disposed || _suspended || !_canRun() || _isPrivate()) return false;
        var matches = Items.Where(entry => entry.Enabled && entry.Runtime.IsRunning && entry.Saved.Macro is { Kind: MacroKind.Shortcut } macro && macro.Pattern == key).ToArray();
        foreach (var entry in matches) entry.Runtime.Publish(new("key", key));
        return matches.Length > 0;
    }
    public void DiscardPartialLine() => Worker.DiscardPartialLine();
    public async Task<bool> HandleCommandAsync(string command)
    {
        foreach (var entry in Items.ToArray())
            if (await entry.Runtime.HandleCommandAsync(command)) return true;
        return false;
    }
    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        if (suspended) Stop();
        else
        {
            // Re-evaluating a script can replay top-level sends. Resume only on an explicit
            // reload/toggle, rather than sending extra commands after an agent step or Stop.
            foreach (var entry in Items.Where(entry => entry.Enabled)) _attempted.Add(entry.Id);
            Changed?.Invoke();
        }
    }

    /// <summary>Stops every script and ends the worker process; the next script to run starts a fresh one.</summary>
    public void Stop()
    {
        foreach (var entry in Items) entry.Runtime.Stop();
        Worker.Shutdown();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Panels.Clear();
        foreach (var entry in Items) { entry.Runtime.Changed -= OnChanged; await entry.Runtime.DisposeAsync(); }
        Worker.Crashed -= OnWorkerCrashed;
        await Worker.DisposeAsync();
    }
}
