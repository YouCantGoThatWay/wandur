using Avalonia.Controls;
using Avalonia.Input;
using Wandur.Core.Discovery;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class BrowserTestWindow : Window
{
    public BrowserTestWindow(WorldCatalog catalog, SessionWorkspace sessions)
        : this(new WorldBrowserViewModel(catalog, sessions), catalog) { }

    public BrowserTestWindow(WorldBrowserViewModel model, WorldCatalog catalog)
    {
        Title = L.FindAMUDWandur; Width = 1100; Height = 780; MinWidth = 680; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var browser = new WorldBrowserView(model, catalog);
        Content = browser;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => { model.Dispose(); };
    }
}
