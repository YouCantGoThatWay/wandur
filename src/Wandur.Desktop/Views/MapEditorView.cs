using Avalonia.Controls;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop.Views;

/// <summary>A document-sized map canvas with a separate, resizable editing inspector.</summary>
public sealed class MapEditorView : UserControl
{
    public MapEditorView(MapViewModel model)
    {
        DataContext = model;
        Content = new MapView(model, editingWorkspace: true);
    }
}
