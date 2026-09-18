using Wandur.Core.Protocol;
using Wandur.Core.Sessions;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    private sealed record PendingProtocolDiagnostic(DateTimeOffset ReceivedAt, byte Option, byte[]? Payload, ProtocolDiagnosticContent Content);
    private readonly Queue<(IMudSession Session, long Epoch, PendingProtocolDiagnostic Message)> _pendingDiagnostics = new();
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
            .Matches(new(profile.Host, profile.Port, profile.UseTls))) return;
        WorldTheme = profile.Theme is { IsValid: true } theme ? theme : null;
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
            // Retain one extra byte so the formatter can report that a payload was truncated.
            var payload = hidden ? null : message.Payload.Take(ProtocolDiagnosticFormatter.MaximumPayloadBytes + 1).ToArray();
            // Sanitize before queuing. Diagnostic visibility is independent of script/agent privacy.
            var content = ProtocolDiagnosticFormatter.Format(message.Option, message.Payload, hidden, _diagnosticSecrets);
            var diagnostic = new PendingProtocolDiagnostic(DateTimeOffset.UtcNow, message.Option, payload, content);
            var size = DiagnosticSize(diagnostic);
            while (_pendingDiagnostics.Count > 0 && (_pendingDiagnosticBytes + size > 524_288 || _pendingDiagnostics.Count >= 200))
                _pendingDiagnosticBytes -= DiagnosticSize(_pendingDiagnostics.Dequeue().Message);
            _pendingDiagnostics.Enqueue((session, hidden ? -1 : _scriptOutputEpoch,
                diagnostic));
            _pendingDiagnosticBytes += size;
        }
    }
}
