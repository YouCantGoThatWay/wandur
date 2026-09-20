using Avalonia.Controls;
using Wandur.Core.Settings;
using Wandur.Core.Terminal;

namespace Wandur.Desktop.Terminal;

public interface ITranscriptDisplay : IDisposable
{
    Control View { get; }
    string PlainText { get; }
    void ApplySettings(ClientSettings settings);
    void FollowTail();
    bool IsFollowingTail { get; }
    /// <summary>Whether the reader has text selected in the transcript, which a click must leave alone.</summary>
    bool HasSelection { get; }
    /// <summary>The buffer row drawn at the top of the viewport; read before a resize and written after it to keep the reader's place.</summary>
    int ViewportTop { get; set; }
    /// <summary>The text of that first visible row, which is what a reader watches for when the layout moves under them.</summary>
    string TopVisibleText { get; }
    event Action? ViewportChanged;
    /// <summary>A right click on the transcript: the line under the pointer and the selection, for a menu.</summary>
    event Action<TranscriptContext>? MenuRequested;
    /// <summary>Copies the selection to the clipboard; false when nothing was selected or there is no clipboard.</summary>
    Task<bool> CopySelectionAsync();
}

/// <summary>What was under the pointer when the transcript's menu was asked for.</summary>
public sealed record TranscriptContext(string? Line, string? Selection, Control Anchor);

public interface ITranscriptDisplayFactory
{
    ITranscriptDisplay Create(AnsiTerminal transcript);
}

public sealed class TranscriptDisplayFactory : ITranscriptDisplayFactory
{
    public ITranscriptDisplay Create(AnsiTerminal transcript) => new TranscriptDisplay(transcript);
}
