using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Wandur.Desktop;

internal static class Ui
{
    public static TextBlock Text(string text, double size = 13, string? cssClass = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (cssClass is not null) block.Classes.Add(cssClass);
        return block;
    }

    public static TextBlock TextKey(string key, double size = 13, string? cssClass = null)
    {
        var block = Text("", size, cssClass);
        block.Bind(TextBlock.TextProperty, LocalizedText.Binding(key));
        return block;
    }
    public static Button ButtonKey(string key, Action action, string? cssClass = null)
    {
        var button = Button("", action, cssClass);
        button.Bind(ContentControl.ContentProperty, LocalizedText.Binding(key));
        return button;
    }

    public static Button Button(string label, Action action, string? cssClass = null)
    {
        var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Classes.Add("app-button");
        if (cssClass is not null) button.Classes.Add(cssClass);
        button.Click += (_, _) => action();
        return button;
    }

    public static StackPanel Stack(params Control[] controls) => new StackPanel { Spacing = 12 }.WithChildren(controls);
    public static T ToolbarIcon<T>(T button, string geometry, string tip) where T : Button
    {
        button.Classes.Add("command-bar-button");
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(geometry), Width = 15, Height = 15, Stretch = Stretch.Uniform,
            StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding(nameof(button.Foreground)) { Source = button });
        button.Content = icon;
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    /// <summary>A filled glyph that follows the foreground of the control it sits in.</summary>
    public static Avalonia.Controls.Shapes.Path Glyph(string geometry, TemplatedControl source, double size = 14)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(geometry), Width = size, Height = size, Stretch = Stretch.Uniform,
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new Avalonia.Data.Binding(nameof(source.Foreground)) { Source = source });
        return icon;
    }

    public static T ToolbarIconKey<T>(T button, string geometry, string key) where T : Button
    {
        ToolbarIcon(button, geometry, "");
        button.Bind(ToolTip.TipProperty, LocalizedText.Binding(key));
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, LocalizedText.Binding(key));
        return button;
    }

    public static Border Toolbar(Control content, string name)
    {
        var bar = new Border { Name = name, Padding = new Thickness(8, 3), BorderThickness = new Thickness(0, 0, 0, 1), Child = content };
        bar.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ChromeBrush"));
        bar.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        return bar;
    }

    private static StackPanel WithChildren(this StackPanel stack, IEnumerable<Control> controls) { foreach (var control in controls) stack.Children.Add(control); return stack; }
    public static Border Card(Control child, double padding = 16)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        border.Bind(Border.CornerRadiusProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("CardCornerRadius"));
        border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PanelBrush"));
        return border;
    }

    public static Control FieldKey(string key, Control input) => new StackPanel { Spacing = 6, Children = { TextKey(key, 12, "muted"), input } };
    public static Control Field(string label, Control input) => new StackPanel { Spacing = 6, Children = { Text(label, 12, "muted"), input } };
}
