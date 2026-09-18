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
