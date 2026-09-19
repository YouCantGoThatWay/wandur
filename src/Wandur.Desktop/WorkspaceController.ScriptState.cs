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

    // Names the cache took while scripts could not have the event (the login handshake owns the session), replayed
    // as ordinary events once public play resumes. Names only: the replay reads the cache at that moment, so a
    // value a live event has since superseded is never delivered again in its older form. Kept in wire order, and
    // bounded by the cache since only a name it accepted is added.
    private readonly OrderedDictionary<string, bool> _replayMsdp = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, bool> _replayGmcp = new(StringComparer.Ordinal);
    private const int ReplayBatch = 32;

    private void ResetScriptState(string worldKey)
    {
        ClearScriptState();
        _scriptReportsSent.Clear();
        if (_scriptReportWorldKey != worldKey) { _scriptReportRequests.Clear(); _scriptReportWorldKey = worldKey; }
    }

    private void ClearScriptState()
    {
        ScriptState.Clear();
        _replayMsdp.Clear(); _replayGmcp.Clear();
    }

    /// <summary>Delivers cached values that scripts never got as events, one event per variable or package, while
    /// play is public. At most 32 per call, from the output timer, so a replay of a full cache (512 entries) never
    /// overflows a script's queue of 128 next to live traffic. A script that starts during a replay is seeded from
    /// the same cache and may also get some of these events; its handlers redraw from the state, so a duplicate
    /// is harmless and no deduplication is attempted.</summary>
    private void ReplayCachedState()
    {
        if (IsPrivate || _login is not null || (_replayMsdp.Count == 0 && _replayGmcp.Count == 0)) return;
        var budget = ReplayBatch;
        while (budget-- > 0 && _replayMsdp.Count > 0)
        {
            var variable = _replayMsdp.GetAt(0).Key; _replayMsdp.RemoveAt(0);
            if (ScriptState.TryGetMsdp(variable) is { } json) ScriptLibrary.Publish(MsdpScriptEvents.Event(variable, json));
        }
        while (budget-- > 0 && _replayGmcp.Count > 0)
        {
            var package = _replayGmcp.GetAt(0).Key; _replayGmcp.RemoveAt(0);
            if (ScriptState.TryGetGmcp(package) is { } json) ScriptLibrary.Publish(new("gmcp", json == "null" ? package : package + " " + json));
        }
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
