using System.Text;
using Wandur.Core.Protocol;
using Wandur.Core.Sessions;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    private sealed record PendingProtocolDiagnostic(DateTimeOffset ReceivedAt, byte Option, byte[]? Payload, ProtocolDiagnosticContent Content);
    private readonly Queue<(IMudSession Session, long Epoch, long CacheEpoch, PendingProtocolDiagnostic Message)> _pendingDiagnostics = new();
    private int _pendingDiagnosticBytes;
    private volatile string[] _diagnosticSecrets = [];
    private void RememberDiagnosticSecret(string value)
    {
        if (value.Length is > 0 and <= 8192 && !_diagnosticSecrets.Contains(value, StringComparer.Ordinal))
            _diagnosticSecrets = [value, .. _diagnosticSecrets.Take(7)];
    }
    private static int DiagnosticSize(PendingProtocolDiagnostic message) => (message.Payload?.Length ?? 0) + message.Content.Body.Length * 2;

    private ProtocolBindingEngine? _protocolBindings;
    private readonly Wandur.Models.GameState _emptyGameState = new();
    public Wandur.Models.GameState GameState => _protocolBindings?.State ?? _emptyGameState;

    private bool _mappingRefreshPending;
    private bool _mappingRefreshInFlight;

    internal void ApplyCatalogProfile(Wandur.Core.Settings.ConnectionProfile profile)
    {
        if (_disposed || _mapProfile is null || !new Wandur.Models.WorldEndpoint(_mapProfile.Host, _mapProfile.Port, _mapProfile.UseTls)
            .Matches(new(profile.Host, profile.Port, profile.UseTls)))
        {
            Wandur.Core.Diagnostics.ThemeTrace.Write("tab.catalogProfile",
                $"ignored for {profile.Host}:{profile.Port}: disposed={_disposed} mapProfile={(_mapProfile is null ? "null" : $"{_mapProfile.Host}:{_mapProfile.Port}")}");
            return;
        }
        WorldTheme = profile.Theme is { IsValid: true } theme ? theme : null;
        Wandur.Core.Diagnostics.ThemeTrace.Write("tab.catalogProfile",
            $"{profile.Host}:{profile.Port} -> theme={WorldTheme?.Id ?? "(none)"}");
        if (profile.GetProtocolMapping() is { } mapping)
        {
            var changed = _protocolBindings is null || _protocolBindings.UpdateMapping(mapping);
            _protocolBindings ??= new(mapping);
            if (changed) { _mapProfile = _mapProfile with { ProtocolMapping = mapping }; _mappingRefreshPending = true; _ = RefreshMappedProtocolSubscriptionAsync(); }
        }
        Changed?.Invoke();
    }

    private async Task RefreshMappedProtocolSubscriptionAsync()
    {
        if (!_mappingRefreshPending || _mappingRefreshInFlight || IsPrivate || _login is not null ||
            _session is not TelnetSession session || !session.IsConnected || session.ProtocolState is not { Gmcp: TelnetOptionState.Enabled } and not { Msdp: TelnetOptionState.Enabled }) return;
        _mappingRefreshPending = false;
        _mappingRefreshInFlight = true;
        try
        {
            if (!await session.RefreshProtocolSubscriptionsAsync(_mapProfile?.GetProtocolMapping()) && ReferenceEquals(_session, session))
                _mappingRefreshPending = true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or ObjectDisposedException)
        { /* Fresh observations can still arrive normally; reconnect performs standard negotiation. */ }
        finally
        {
            _mappingRefreshInFlight = false;
        }
    }

    public ProtocolDiagnosticsViewModel Diagnostics { get; } = new();

    private void ReceiveProtocol(IMudSession session, TelnetDataMessage message, bool wirePrivate)
    {
        lock (_pendingLock)
        {
            var hidden = message.MayContainPrivateText || wirePrivate || _scriptPrivacyBlocked;
            // The script state cache is gated by privacy alone, so a payload received during login is kept
            // for it; the flush still hands scripts, bindings and the agent nothing while login runs.
            // While only the login makes input private, the parser flags every packet, so that flag is
            // replaced by what it would otherwise mean: a server echo interval or a GMCP login message.
            var loginOnly = _scriptPrivacyBlocked && !_cachePrivate;
            var cacheHidden = wirePrivate || _cachePrivate || (message.MayContainPrivateText && !loginOnly) ||
                (message.Option == 201 && GmcpLoginProtocol.IsPrivate(Encoding.UTF8.GetString(message.Payload)));
            // Retain one extra byte so the formatter can report that a payload was truncated.
            var payload = cacheHidden ? null : message.Payload.Take(ProtocolDiagnosticFormatter.MaximumPayloadBytes + 1).ToArray();
            // Sanitize before queuing. Diagnostic visibility is independent of script/agent privacy.
            var content = ProtocolDiagnosticFormatter.Format(message.Option, message.Payload, hidden, _diagnosticSecrets);
            var diagnostic = new PendingProtocolDiagnostic(DateTimeOffset.UtcNow, message.Option, payload, content);
            var size = DiagnosticSize(diagnostic);
            while (_pendingDiagnostics.Count > 0 && (_pendingDiagnosticBytes + size > 524_288 || _pendingDiagnostics.Count >= 200))
                _pendingDiagnosticBytes -= DiagnosticSize(_pendingDiagnostics.Dequeue().Message);
            _pendingDiagnostics.Enqueue((session, hidden ? -1 : _scriptOutputEpoch, cacheHidden ? -1 : _cacheEpoch,
                diagnostic));
            _pendingDiagnosticBytes += size;
        }
    }
}
