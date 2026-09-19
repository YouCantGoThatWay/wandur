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
    /// <summary>The buffer row drawn at the top of the viewport; read before a resize and written after it to keep the reader's place.</summary>
    int ViewportTop { get; set; }
    /// <summary>The text of that first visible row, which is what a reader watches for when the layout moves under them.</summary>
    string TopVisibleText { get; }
    event Action? ViewportChanged;
}

public interface ITranscriptDisplayFactory
{
    ITranscriptDisplay Create(AnsiTerminal transcript);
}

public sealed class TranscriptDisplayFactory : ITranscriptDisplayFactory
{
    public ITranscriptDisplay Create(AnsiTerminal transcript) => new TranscriptDisplay(transcript);
}
