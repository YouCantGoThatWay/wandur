namespace Wandur.Core.Sessions;

public sealed record SessionStatus(bool Connected, string Message);
public sealed record ReceivedSessionText(string Text, bool MayContainPrivateText);

public interface IMudSession : IAsyncDisposable
{
    event Action<string>? Output;
    event Action<SessionStatus>? StatusChanged;
    event Action<bool>? PrivateInputChanged;
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
}
