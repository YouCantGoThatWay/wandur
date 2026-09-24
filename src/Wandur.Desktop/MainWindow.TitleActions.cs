using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Wandur.Core.Localization;

namespace Wandur.Desktop;

public sealed partial class MainWindow
{
    private const double TitleActionsWidth = 80;
    private const string FullScreenGlyph = "M 1,6 V 1 H 6 M 10,1 H 15 V 6 M 15,10 V 15 H 10 M 6,15 H 1 V 10";
    private const string ExitFullScreenGlyph = "M 1,6 H 6 V 1 M 10,1 V 6 H 15 M 15,10 H 10 V 15 M 6,15 V 10 H 1";
    private StackPanel _titleActions = null!;
    private Button _exitFullScreenButton = null!;
    private ThemeMenuButton _themeMenuButton = null!;
    private bool _fullScreenChrome;
    private WindowState _beforeFullScreen = WindowState.Normal;

    private double TitleActionsRightInset =>
        Math.Max(WindowDecorationMargin.Right, OperatingSystem.IsWindows() ? 144 : 0) + 12;

    private void InitializeTitleActions()
    {
        _themeMenuButton = new ThemeMenuButton(() => Controller);
        var fullScreen = ToolbarButton(FullScreenGlyph, "TitleFullScreenButton", nameof(Strings.FullScreen));
        fullScreen.Width = fullScreen.Height = 30;
        fullScreen.Click += (_, _) => ToggleFullScreen();
        _titleActions = new StackPanel
        {
            Name = "TitleActions", Orientation = Orientation.Horizontal, Spacing = 4,
            Width = TitleActionsWidth, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Children = { _themeMenuButton, fullScreen }
        };
        WindowDecorationProperties.SetElementRole(_titleActions, WindowDecorationsElementRole.User);
        _chrome.Children.Add(_titleActions);
        PositionTitleActions();
        _exitFullScreenButton = ToolbarButton(ExitFullScreenGlyph, "ExitFullScreenButton", nameof(Strings.ExitFullScreen));
        _exitFullScreenButton.Width = _exitFullScreenButton.Height = 28;
        _exitFullScreenButton.IsVisible = false;
        _exitFullScreenButton.Click += (_, _) => ToggleFullScreen();
        _toolbarActions.Children.Add(_exitFullScreenButton);
    }

    private void PositionTitleActions() =>
        _titleActions.Margin = new Thickness(0, 10, TitleActionsRightInset, 0);

    internal void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? _beforeFullScreen : WindowState.FullScreen;

    private void ApplyFullScreenChrome()
    {
        _fullScreenChrome = true;
        _themeMenuButton.Flyout?.Hide();
        _titleActions.IsVisible = false;
        _plaqueTitleHost.IsVisible = false;
        _metalDrag.IsVisible = false;
        _titleBarIdentity.IsVisible = false;
        _appTitle.IsVisible = false;
        _ornaments.IsVisible = false;
        // Keep content and its theme intact, but release all decorative title/frame insets.
        // The next windowed pass reloads these values from the current theme, not an old snapshot.
        _windowSkin.BandHeight = 0;
        _windowSkin.BorderBitmap = null;
        _windowSkin.Inset = default;
        _windowSkin.TitleModuleBounds = default;
        _bezel.BorderBitmap = null;
        _bezel.Inset = default;
        ApplyToolbarSlot(null);
        UpdateTitleBarInsets();
        ExtendClientAreaTitleBarHeightHint = 0;
        _toolbar.Background = FleetSkin.Toolbar;
        PlaceFullScreenExit();
    }

    private void PlaceFullScreenExit()
    {
        // View > Hide Toolbar must not strand mouse users in fullscreen. Reuse the
        // existing footer row in that case, without adding another row of chrome.
        if (_footer.Child is not Grid footer) return;
        Panel target = ToolbarVisible ? _toolbarActions : footer;
        if (!ReferenceEquals(_exitFullScreenButton.Parent, target))
        {
            if (_exitFullScreenButton.Parent is Panel old) old.Children.Remove(_exitFullScreenButton);
            if (ReferenceEquals(target, footer))
            {
                if (footer.ColumnDefinitions.Count == 2) footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                Grid.SetColumn(_exitFullScreenButton, 2);
            }
            target.Children.Add(_exitFullScreenButton);
        }
        _exitFullScreenButton.IsVisible = true;
    }

    private void RestoreWindowedChrome()
    {
        if (!_fullScreenChrome) return;
        _fullScreenChrome = false;
        _windowSkin.ApplyFromTheme();
        _bezel.ApplyFromTheme();
        _ornaments.ApplyFromTheme();
        _ornaments.IsVisible = true;
        _titleActions.IsVisible = true;
        _appTitle.IsVisible = true;
        _exitFullScreenButton.IsVisible = false;
        _fleetToolbarSurfaceKey = null;
        BindToolbarBackground();
    }
}
