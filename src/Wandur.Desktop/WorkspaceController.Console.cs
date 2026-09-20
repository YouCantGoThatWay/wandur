using Wandur.Core.Diagnostics;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    /// <summary>
    /// The raw text stream behind the transcript and the commands sent back, for the Diagnostics console. Fed from
    /// the same points that feed the transcript (the flush, the send path and script echoes) under the diagnostics
    /// privacy rule: a chunk stamped private on receipt, or sent while input is private or a login is running, is
    /// logged as a marker only. Appending is a ring insert; the view subscribes when it is on screen.
    /// </summary>
    public ConsoleLog ConsoleLog { get; } = new();

    private void LogConsoleReceived(string text, bool hidden) => ConsoleLog.Append(ConsoleEntryKind.Received, text, hidden, _diagnosticSecrets);
    private void LogConsoleSent(string command, bool hidden) => ConsoleLog.Append(ConsoleEntryKind.Sent, command, hidden || _login is not null, _diagnosticSecrets);
    private void LogConsoleScript(string text) => ConsoleLog.Append(ConsoleEntryKind.Script, text, IsPrivate || _login is not null, _diagnosticSecrets);
}
