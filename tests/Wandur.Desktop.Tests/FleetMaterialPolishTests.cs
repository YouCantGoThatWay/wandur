using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;

namespace Wandur.Desktop.Tests;

public sealed class FleetMaterialPolishTests
{
    [AvaloniaFact]
    public void DarkWorldAccentGetsReadablePrimaryButtonText()
    {
        var basis = UserTheme.FromPreset("Hull").ToWorldTheme();
        var world = basis with { Colors = basis.Colors with { Accent = "#15232B", AccentSecondary = "#15232B" } };
        try
        {
            ThemeService.Apply(new ClientSettings(), world);
            var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.Resources["PrimaryTextBrush"]).Color;
            var face = Assert.IsType<LinearGradientBrush>(Application.Current.Resources["PrimaryFaceBrush"]);
            foreach (var stop in face.GradientStops)
                Assert.True(ContrastProbe.Contrast(foreground, stop.Color) >= 4.5, "Dark accent buttons need light text.");
        }
        finally { ThemeService.Apply(new ClientSettings()); }
    }

    [AvaloniaFact]
    public void ChangingTerminalColorsDoesNotRepaintTheTitlePlate()
    {
        var dark = DefaultSkin.For("#212428", "#E2E4E7", "#101418", "#DFE4E9", "#80B5C0");
        var light = DefaultSkin.For("#212428", "#E2E4E7", "#FFFFFF", "#222222", "#80B5C0");
        Assert.Equal(dark.Layout!.TitleBar!.Plaque!.Fill, light.Layout!.TitleBar!.Plaque!.Fill);
        Assert.Equal(dark.Surfaces, light.Surfaces);
    }

    [AvaloniaTheory]
    [InlineData("Hull", false)]
    [InlineData("Hull", true)]
    [InlineData("Slate", false)]
    public async Task WorkingControlsHaveBalancedTargetsAndThinFrames(string theme, bool lightTerminal)
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-material-" + Guid.NewGuid(), "settings.json"));
        store.Save(new ClientSettings { Theme = theme, UseWorldThemes = false,
            Background = lightTerminal ? "#FFFFFF" : null, Foreground = lightTerminal ? "#222222" : null });
        var window = new MainWindow(new TranscriptDisplayFactory(), store, new MemoryPasswordVault(),
            new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        try
        {
            window.Show(); await window.Controller.StartAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var frame = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FleetDocumentFrame");
            Assert.True(frame.Padding.Left + frame.BorderThickness.Left <= 2,
                "The document frame should not consume more than two DIPs per side.");
            var docks = window.GetVisualDescendants().OfType<ThemeDockSkinHost>().ToArray();
            Assert.NotEmpty(docks);
            Assert.All(docks, dock => Assert.InRange(dock.Child!.Bounds.Left, 0, 2));
            var fit = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FitMapFloor");
            Assert.InRange(fit.Bounds.Width / fit.Bounds.Height, .95, 1.05);
            Assert.True(fit.Bounds.Width >= 26, "An icon button needs room around its glyph.");
            var mapInk = Assert.IsAssignableFrom<ISolidColorBrush>(fit.Foreground).Color;
            // Use the painted control surface, not the gradient's bottom bevel pixel,
            // which is outside the inset glyph and intentionally dark.
            Assert.True(ContrastProbe.Contrast(mapInk, ContrastProbe.Surface(fit, Colors.Transparent)) >= 4.5,
                "Toolbar text must remain readable independently of the terminal colors.");
            if (theme == "Hull")
            {
                var highlight = Assert.IsAssignableFrom<ISolidColorBrush>(FleetSkin.RimHighlight).Color;
                Assert.True(highlight.R >= 230, "Silver should retain a light, palette-derived bevel.");
            }
            var input = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CommandInput");
            var send = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SendCommand");
            Assert.Equal(input.Bounds.Height, send.Bounds.Height, 1);
            // Fluent template states must inherit the instrument ink, not the shell's dark ink.
            foreach (var name in new[] { "LookButton", "MapToolsToggle" })
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
                Assert.Equal(button.Foreground, presenter.Foreground);
                button.IsEnabled = false; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(button.Foreground, presenter.Foreground);
            }
            var headers = window.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Name == "PART_Border" && b.TemplatedParent is Dock.Avalonia.Controls.ToolChromeControl).ToArray();
            Assert.NotEmpty(headers);
            if (theme == "Slate")
            {
                var edge = Assert.IsAssignableFrom<ISolidColorBrush>(frame.BorderBrush).Color;
                Assert.True(edge.R < 140 && edge.G < 140 && edge.B < 140,
                    "Graphite must not inherit the silver frame's white outline.");
                foreach (var header in headers)
                    foreach (var shadow in header.BoxShadow)
                        Assert.True(shadow.Color.A < 128 || shadow.Color.R < 160,
                            "Dark headers must not inherit an opaque white bevel.");
            }
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); }
    }
}
