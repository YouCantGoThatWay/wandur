using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed class SessionScriptsButton : Button
{
    public SessionScriptsButton(SessionAutomationViewModel model, Action<int>? editConfiguration)
    {
        Name = "SessionScripts";
        Ui.ToolbarIconKey(this, "M 2,4 H 14 M 2,8 H 14 M 2,12 H 14 M 5,2 V 6 M 11,6 V 10 M 7,10 V 14", nameof(L.ScriptsButton));
        Width = double.NaN; Height = 26; MinHeight = 0; Padding = new Thickness(6, 0);
        var icon = (Control)Content!;
        Content = null;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon, Ui.TextKey(nameof(L.ScriptsButton), 11) } };
        var items = new ItemsControl { ItemsSource = model.Scripts };
        items.ItemTemplate = new FuncDataTemplate<ScriptLibraryItemViewModel>((_, _) =>
        {
            var name = Ui.Text("", 12); name.Bind(TextBlock.TextProperty, new Binding(nameof(ScriptLibraryItemViewModel.Name)));
            var status = Ui.Text("", 10, "muted"); status.Bind(TextBlock.TextProperty, new Binding(nameof(ScriptLibraryItemViewModel.Status)));
            var toggle = new CheckBox { HorizontalAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Spacing = 2, Children = { name, status } }, Margin = new Thickness(0, 3) };
            toggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ScriptLibraryItemViewModel.Enabled)) { Mode = BindingMode.TwoWay });
            return toggle;
        });
        var reload = Ui.ToolbarIconKey(new Button { Name = "ReloadSessionAutomation", Command = model.ReloadCommand },
            "M 13,5 A 6,6 0 1 0 14,10 M 13,1 V 5 H 9", nameof(L.AutomationReload));
        var heading = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(reload, Avalonia.Controls.Dock.Right); heading.Children.Add(reload);
        heading.Children.Add(Ui.TextKey(nameof(L.ScriptsButton), 15));
        var edit = Ui.ButtonKey(nameof(L.AutomationEdit), () => { Flyout?.Hide(); editConfiguration?.Invoke(2); });
        edit.Name = "EditSessionAutomation"; edit.IsEnabled = editConfiguration is not null;
        var error = Ui.Text("", 12); error.Bind(TextBlock.TextProperty, new Binding(nameof(model.Error))); error.Bind(IsVisibleProperty, new Binding(nameof(model.HasError)));
        var empty = Ui.TextKey(nameof(L.AutomationEmpty), 12, "muted"); empty.Bind(IsVisibleProperty, new Binding(nameof(model.IsEmpty)));
        var panel = new StackPanel { DataContext = model, Width = 340, Spacing = 10, Children =
        {
            heading,
            Ui.TextKey(nameof(L.AutomationLiveHint), 11, "muted"),
            new ScrollViewer { Content = items, MaxHeight = 320 }, empty, error,
            edit
        } };
        Flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, Content = new ScrollViewer { MaxHeight = 620, Content = panel } };
    }
}
