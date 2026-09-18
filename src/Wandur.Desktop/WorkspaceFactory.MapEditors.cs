using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop;

public sealed class MapEditorDocument : Document
{
    public required WorkspaceController Controller { get; init; }
    public required RoomMapTracker WorldMap { get; init; }
    public required MapViewModel Model { get; init; }
    public bool IsClosed { get; private set; }
    public Action? SelectSession { get; init; }
    public Action? OnClosed { get; init; }
    public override void OnSelected() { SelectSession?.Invoke(); base.OnSelected(); }
    public override bool OnClose()
    {
        IsClosed = true;
        Model.Detach();
        OnClosed?.Invoke();
        return true;
    }
}

public sealed partial class WorkspaceFactory
{
    private readonly List<MapEditorDocument> _mapEditors = [];
    public IReadOnlyList<MapEditorDocument> MapDocuments => _mapEditors;

    public void OpenMapEditor(WorkspaceController controller, MapViewModel? source = null)
    {
        if (_documents is null || !controller.HasSession || source?.IsExercise == true) return;
        PruneMapDocuments();
        var document = _mapEditors.FirstOrDefault(d => ReferenceEquals(d.Controller, controller));
        if (document is null)
        {
            var model = new MapViewModel(controller);
            model.Attach();
            model.IsEditMode = true;
            if ((source?.SelectedRoomId ?? controller.Map.Snapshot.CurrentRoomId) is { } selected) model.SelectRoom(selected);
            model.Detach();
            MapEditorDocument? created = null;
            document = created = new MapEditorDocument
            {
                Id = "map-editor-" + Guid.NewGuid().ToString("N"),
                Title = L.Format(L.MapEditorTitle, controller.WorldName),
                Controller = controller, WorldMap = controller.Map, Model = model,
                CanClose = true, CanFloat = true,
                OnClosed = () => { if (created is not null) { _mapEditors.Remove(created); if (ReferenceEquals(SelectedKey, created)) SessionSelected(); } UpdateDocumentTabs(); },
                SelectSession = () => EditorSelected(created, controller)
            };
            _mapEditors.Add(document);
            UpdateDocumentTabs();
            AddDockable(_sessionDocument?.Owner as IDock ?? _documents, document);
        }
        EditorSelected(document, controller);
        SetActiveDockable(document);
        if (document.Owner is IDock owner) SetFocusedDockable(owner, document);
    }

    private void PruneMapDocuments()
    {
        foreach (var document in _mapEditors.ToArray())
            if (!document.IsClosed && (!sessions.Tabs.Any(t => ReferenceEquals(t.Controller, document.Controller)) ||
                !ReferenceEquals(document.WorldMap, document.Controller.Map))) CloseDockable(document);
    }

    public void PruneEditorDocuments() { PruneMapDocuments(); }

    public void CloseEditorDocuments()
    {
        foreach (var document in _mapEditors.ToArray()) if (!document.IsClosed) CloseDockable(document);
        _mapEditors.Clear();
        UpdateDocumentTabs();
    }
}
