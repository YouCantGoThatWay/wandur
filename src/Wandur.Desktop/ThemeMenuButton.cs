using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Wandur.Core.Settings;
using L = Wandur.Core.Localization.Strings;
using Shape = Avalonia.Controls.Shapes.Shape;

namespace Wandur.Desktop;

/// <summary>A theme menu that follows the active session at opening and selection time.</summary>
internal sealed class ThemeMenuButton : Button
{
    private readonly Func<WorkspaceController> _controller;
    private readonly MenuFlyout _menu = new();

    protected override Type StyleKeyOverride => typeof(Button);

    public ThemeMenuButton(Func<WorkspaceController> controller)
    {
        _controller = controller;
        Name = "TitleThemeButton";
        Classes.Add("command-bar-button");
        Width = 44;
        Height = 30;
        MinWidth = MinHeight = 0;
        Padding = new Thickness(6, 4);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.Theme)));
        Bind(AutomationProperties.NameProperty, LocalizedText.Binding(nameof(L.Theme)));
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5, IsHitTestVisible = false,
            Children =
            {
                // An original palette outline, with three paint wells and a thumb opening.
                Glyph("M 9,1 C 4.4,1 1,4.5 1,9 C 1,13.5 4.5,17 9,17 " +
                    "C 11.2,17 12.2,15.6 11.2,14.1 C 10.3,12.7 11.1,11.4 12.8,11.5 " +
                    "C 15.8,11.8 17,10.4 17,8 C 17,4.1 13.5,1 9,1 Z " +
                    "M 4.5,7 A 0.7,0.7 0 1 0 5.9,7 A 0.7,0.7 0 1 0 4.5,7 Z " +
                    "M 7.5,4.5 A 0.7,0.7 0 1 0 8.9,4.5 A 0.7,0.7 0 1 0 7.5,4.5 Z " +
                    "M 11.5,5 A 0.7,0.7 0 1 0 12.9,5 A 0.7,0.7 0 1 0 11.5,5 Z", 18, 18),
                Glyph("M 1,1 L 4,4 L 7,1", 7, 4)
            }
        };
        _menu.Opening += (_, _) => WithController(RebuildMenu);
        Flyout = _menu;
    }

    private Avalonia.Controls.Shapes.Path Glyph(string geometry, double width, double height)
    {
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(geometry), Width = width, Height = height,
            Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        glyph.Bind(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        return glyph;
    }

    private void RebuildMenu(WorkspaceController controller)
    {
        _menu.Items.Clear();
        var settings = controller.Settings;
        var followsWorld = settings.UseWorldThemes && controller.WorldTheme is { IsValid: true };
        foreach (var id in UserTheme.PresetNames)
            AddTheme(id, UserTheme.DisplayName(id), UserTheme.FromPreset(id), !followsWorld && settings.Theme == id);
        if (settings.CustomThemes.Count > 0)
        {
            _menu.Items.Add(new Separator());
            foreach (var theme in settings.CustomThemes)
                AddTheme(theme.Id, theme.Name, theme, !followsWorld && settings.Theme == theme.Id);
        }
        _menu.Items.Add(new Separator());
        var follow = new MenuItem
        {
            Header = L.FollowMudTheme, ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = settings.UseWorldThemes
        };
        AutomationProperties.SetName(follow, L.FollowMudTheme);
        follow.Click += (_, _) => WithController(active =>
        {
            var current = active.Settings;
            active.SaveSettings(current with { UseWorldThemes = !current.UseWorldThemes });
        });
        _menu.Items.Add(follow);
        // The presenter is created before Opening. Publish the rebuilt snapshot so
        // its item view cannot retain the empty backing collection from first use.
        if (_menu.Popup.Child is MenuFlyoutPresenter presenter)
            presenter.ItemsSource = _menu.Items.ToArray();
    }

    private void AddTheme(string id, string label, UserTheme theme, bool isChecked)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var key in new[] { "Terminal", "Accent" })
        {
            var swatch = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.Parse(theme.Colors[key])),
                BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            swatch.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
            AutomationProperties.SetName(swatch, $"{label}: {theme.Colors[key]}");
            header.Children.Add(swatch);
        }
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked };
        AutomationProperties.SetName(item, label);
        item.Click += (_, _) => WithController(active =>
            active.SaveSettings(active.Settings with { Theme = id, UseWorldThemes = false }));
        _menu.Items.Add(item);
    }

    private void WithController(Action<WorkspaceController> action)
    {
        var controller = _controller();
        try { action(controller); }
        catch (Exception error) { controller.ShowNotice(error.Message); }
    }
}
