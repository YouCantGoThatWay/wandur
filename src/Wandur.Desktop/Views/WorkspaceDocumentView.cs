using Avalonia.Controls;
using Avalonia.Data;
using Dock.Model.Core;
using Dock.Settings;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Compact document grip and close action, independent of tab-strip navigation.</summary>
public sealed class WorkspaceDocumentView : UserControl
{
    public WorkspaceDocumentView(IDockable document, Control content)
    {
        var title = Ui.Text("", 12, "muted");
        title.Bind(TextBlock.TextProperty, new Binding(nameof(IDockable.Title)) { Source = document });
        var close = Ui.Button("×", () => document.Factory?.CloseDockable(document));
        close.Classes.Add("tab-close");
        close.Bind(ToolTip.TipProperty, LocalizedText.Binding(nameof(L.CloseWorkspaceItem)));
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title, close }, DataContext = document };
        Grid.SetColumn(close, 1);
        DockProperties.SetIsDragArea(bar, true);
        var toolbar = Ui.Toolbar(bar, "WorkspaceDocumentHeader");
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { toolbar, content } };
        Grid.SetRow(content, 1);
    }
}
