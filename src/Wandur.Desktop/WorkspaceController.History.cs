using Avalonia.Threading;
using Wandur.Core.History;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    private readonly IHistoryStore? _historyStore;
    private HistoryRecorder? _historyRecorder;
    private readonly List<Task> _historyDrains = [];
    private string? _historyWorldKey;
    private bool _historyWarned;
    private int? _historyAppliedDays;

    private void BeginHistory()
    {
        if (_historyRecorder is not null || _historyStore is null || !Settings.HistoryEnabled || _historyWorldKey is null) return;
        _historyWarned = false;
        _historyRecorder = new(_historyStore, new(Guid.NewGuid().ToString("N"), _historyWorldKey,
            WorldName, CharacterName, DateTimeOffset.UtcNow), Settings.HistoryRetentionDays);
        _historyRecorder.Failed += HistoryFailed;
        if (Notice is null) Notice = L.HistoryRecordingNotice;
    }

    private void HistoryFailed() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _historyWarned) return;
        _historyWarned = true;
        ShowNotice(L.HistoryRecordingFailed);
    });

    private void TickHistory()
    {
        if (_historyRecorder is null) return;
        _historyRecorder.UpdateCharacter(CharacterName);
        _historyRecorder.Flush();
        if (!IsConnected && !IsConnecting) EndHistory();
    }

    private void EndHistory()
    {
        if (_historyRecorder is not { } recorder) return;
        recorder.UpdateCharacter(CharacterName);
        _historyDrains.RemoveAll(task => task.IsCompleted);
        _historyDrains.Add(recorder.CompleteAsync());
        _historyRecorder = null;
    }

    internal async Task FlushHistoryAsync()
    {
        if (_historyRecorder is { } recorder)
        {
            recorder.UpdateCharacter(CharacterName);
            await recorder.FlushAsync();
        }
        await Task.WhenAll(_historyDrains);
        _historyDrains.RemoveAll(task => task.IsCompleted);
    }

    // Only saved preferences come through here, never live appearance previews.
    internal void ApplyHistorySettings()
    {
        if (!Settings.HistoryEnabled) EndHistory();
        else if (IsConnected) BeginHistory();
        _historyRecorder?.UpdateRetention(Settings.HistoryRetentionDays);
        if (_historyAppliedDays == Settings.HistoryRetentionDays) return;
        _historyAppliedDays = Settings.HistoryRetentionDays;
        if (_historyStore is not { } store || Settings.HistoryRetentionDays == 0) return;
        var before = DateTimeOffset.UtcNow.AddDays(-Settings.HistoryRetentionDays);
        _historyDrains.RemoveAll(task => task.IsCompleted);
        _historyDrains.Add(Task.Run(() =>
        {
            try { store.Prune(before); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            { HistoryFailed(); }
        }));
    }
}
