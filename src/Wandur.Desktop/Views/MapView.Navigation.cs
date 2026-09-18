using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Platform.Storage;
using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed partial class MapView
{
    partial void AddNavigationControls(StackPanel controls)
    {
        controls.Children.Add(new WrapPanel { Children =
        {
            EditorAction(nameof(L.MapWalk), "MapWalkRoute", Model.WalkRouteCommand),
            EditorAction(nameof(L.MapStopWalk), "MapStopWalk", Model.StopWalkingCommand)
        } });
        foreach (var property in new[] { nameof(Model.WalkStatus) })
        {
            var text = Ui.Text("", 11, "muted"); text.Bind(TextBlock.TextProperty, new Binding(property)); controls.Children.Add(text);
        }
    }
    private Control CreateFileControls()
    {
        var import = EditorAction(nameof(L.MapImport), "MapImport", new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(ImportMapAsync));
        var export = EditorAction(nameof(L.MapExport), "MapExport", new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(ExportMapAsync));
        return new WrapPanel { Children = { import, export } };
    }
    private static FilePickerFileType MapFileType => new(L.MapFileType) { Patterns = ["*.json"], MimeTypes = ["application/json"] };
    private async Task ImportMapAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanOpen) return;
        var tracker = Model.Tracker;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.MapImport, AllowMultiple = false, FileTypeFilter = [MapFileType] });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                if (content.Length + read > MapFileFormat.MaximumBytes) throw new FormatException(L.MapFileType);
                content.Write(buffer, 0, read);
            }
            if (!ReferenceEquals(tracker, Model.Tracker)) return;
            Model.ImportMap(new UTF8Encoding(false, true).GetString(content.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DecoderFallbackException)
        { Model.EditMessage = L.Format(L.MapImportFailed, ex.Message); }
    }
    private async Task ExportMapAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanSave) return;
        try
        {
            var json = Model.ExportMap();
            using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = L.MapExport, SuggestedFileName = "wandur-map.json", DefaultExtension = "json", FileTypeChoices = [MapFileType] });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(json);
            Model.EditMessage = L.MapExportComplete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        { Model.EditMessage = L.Format(L.MapExportFailed, ex.Message); }
    }
}
