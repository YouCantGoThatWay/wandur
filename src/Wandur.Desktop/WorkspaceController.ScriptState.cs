using Wandur.Core.Protocol;
using Wandur.Core.Scripting;
using Wandur.Core.Sessions;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    /// <summary>The latest MSDP variables and GMCP packages of this session. Every script worker is seeded
    /// from it before its source runs, so a value the world sent once, during login or before the script
    /// was enabled, is still there when the script asks for it.</summary>
    internal ProtocolStateCache ScriptState { get; } = new();

    // Stamped on the receiving thread like the script epoch, but moved only by privacy, never by login:
    // MSDP and GMCP payloads are server data, and the login phase is exactly when REPORT answers land.
    private long _cacheEpoch;
    private bool _cachePrivate;

    // Variables scripts asked for, kept for the world so a reconnect asks again; the sent set is per session.
    private readonly HashSet<string> _scriptReportRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scriptReportsSent = new(StringComparer.Ordinal);
    private string? _scriptReportWorldKey;
    private bool _scriptReportInFlight;

    // Login-phase payloads are cached, so a value that carries the saved password verbatim is refused
    // outright, the way the diagnostics view masks it. Protocol data never legitimately contains it.
    private bool ContainsSecret(byte[] payload) => _diagnosticSecrets.Length > 0 && ContainsSecret(System.Text.Encoding.UTF8.GetString(payload));
    private bool ContainsSecret(string text) => _diagnosticSecrets.Any(secret => text.Contains(secret, StringComparison.Ordinal));

    private void ResetScriptState(string worldKey)
    {
        ScriptState.Clear();
        _scriptReportsSent.Clear();
        if (_scriptReportWorldKey != worldKey) { _scriptReportRequests.Clear(); _scriptReportWorldKey = worldKey; }
    }

    private void RequestMsdpReport(string name)
    {
        if (!JavaScriptEngine.IsValidMsdpName(name) || !_scriptReportRequests.Add(name)) return;
        _ = RefreshScriptReportsAsync();
    }

    /// <summary>Sends one REPORT and SEND for every requested variable not yet asked for this session and
    /// not already covered by the world's mapping. Guarded like the mapped refresh: connected, public,
    /// no login in progress, MSDP negotiated. Anything still pending is retried on the next state refresh.</summary>
    private async Task RefreshScriptReportsAsync()
    {
        if (_scriptReportInFlight || IsPrivate || _login is not null || _session is not TelnetSession session ||
            !session.IsConnected || session.ProtocolState.Msdp != TelnetOptionState.Enabled) return;
        _scriptReportInFlight = true;
        try
        {
            while (ReferenceEquals(_session, session) && !IsPrivate && _login is null)
            {
                var mapped = TelnetSession.MappedMsdpNames(_mapProfile?.GetProtocolMapping());
                var names = _scriptReportRequests.Where(name => !_scriptReportsSent.Contains(name) && !mapped.Contains(name)).ToArray();
                if (names.Length == 0) return;
                if (!await session.ReportMsdpAsync(names) || !ReferenceEquals(_session, session)) return;
                _scriptReportsSent.UnionWith(names);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or ObjectDisposedException)
        { /* The connection is closing or closed; a reconnect asks again. */ }
        finally { _scriptReportInFlight = false; }
    }
}
