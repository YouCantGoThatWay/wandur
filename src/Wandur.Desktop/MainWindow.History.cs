using Wandur.Core.History;
using Wandur.Desktop.Views;

namespace Wandur.Desktop;

public sealed partial class MainWindow
{
    private HistoryWindow? _historyWindow;

    internal async Task ShowHistoryAsync()
    {
        if (_closing || _closed) return;
        if (_historyWindow is not null) { _historyWindow.Activate(); return; }
        if (Sessions.HistoryStore is not { } store) return;
        await Task.WhenAll(Sessions.Tabs.Select(tab => tab.Controller.FlushHistoryAsync()));
        if (_closing || _closed) return;
        // A second menu invocation can occur while the pending writes drain.
        if (_historyWindow is not null) { _historyWindow.Activate(); return; }
        var window = new HistoryWindow(store);
        _historyWindow = window;
        window.Closed += (_, _) => _historyWindow = null;
        window.Show(this);
    }
}
