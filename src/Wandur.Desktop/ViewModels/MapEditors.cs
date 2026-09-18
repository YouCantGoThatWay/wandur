using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Wandur.Core.Mapping;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.ViewModels;

public sealed record MapAreaChoice(string Key, string Label);
public sealed record MapDoorChoice(MapDoorState Value, string Label);

public sealed partial class MapRoomEditorViewModel : ObservableObject
{
    private MapRoom? _original;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _area = "";
    [ObservableProperty] private string _x = "0";
    [ObservableProperty] private string _y = "0";
    [ObservableProperty] private string _z = "0";
    [ObservableProperty] private string _environment = "";
    [ObservableProperty] private string _color = "";
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _weight = "1";
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _terrainProvenance = "";
    public string? Id => _original?.Id;
    public bool HasTerrainProvenance => !string.IsNullOrEmpty(TerrainProvenance);

    public void Load(MapRoom room)
    {
        _original = room;
        Name = room.Name; Description = room.Description; Area = room.Area ?? "";
        X = Format(room.X); Y = Format(room.Y); Z = Format(room.Z);
        Environment = room.Environment ?? ""; Color = room.Color ?? "";
        Symbol = room.Symbol ?? ""; Notes = room.Notes; Weight = Format(room.Weight); IsLocked = room.IsLocked;
        TerrainProvenance = MapEnvironmentPalette.UsesInference(room) ? MapEnvironmentPalette.Describe(room) : "";
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(HasTerrainProvenance));
    }

    public bool TryBuild(out MapRoom? room)
    {
        room = null;
        if (_original is null || string.IsNullOrWhiteSpace(Name) || Name.Length > 512 ||
            !Number(X, out var x) || !Number(Y, out var y) || !Number(Z, out var z) ||
            !Number(Weight, out var weight) || weight <= 0 ||
            (!string.IsNullOrWhiteSpace(Color) && !Regex.IsMatch(Color.Trim(), "^#[0-9a-fA-F]{6}$"))) return false;
        room = _original with
        {
            Name = Name.Trim(), Description = Description, Area = Optional(Area), X = x, Y = y, Z = z,
            Environment = Optional(Environment), Color = Optional(Color), Symbol = Optional(Symbol),
            Notes = Notes, Weight = weight, IsLocked = IsLocked, IsManuallyEdited = true
        };
        return true;
    }
    internal static string Format(double number) => number.ToString(CultureInfo.CurrentCulture);
    internal static bool Number(string text, out double number)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) && double.IsFinite(number);
    internal static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

public sealed partial class MapExitEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _direction = "";
    [ObservableProperty] private MapRoom? _destination;
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _weight = "0";
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private MapDoorChoice? _door;
    [ObservableProperty] private bool _createReturnExit;
    [ObservableProperty] private string _returnDirection = "";

    public void Load(MapLink? link, IReadOnlyList<MapRoom> rooms, IReadOnlyList<MapDoorChoice> doors)
    {
        Direction = link?.Direction ?? "";
        Destination = rooms.FirstOrDefault(r => r.Id == link?.ToId);
        Command = link?.Command ?? ""; Weight = MapRoomEditorViewModel.Format(link?.Weight ?? 0);
        IsLocked = link?.IsLocked ?? false;
        Door = doors.First(d => d.Value == (link?.DoorState ?? MapDoorState.None));
        CreateReturnExit = false; ReturnDirection = "";
    }

    public bool TryBuild(string from, out MapLink? link, out MapLink? reverse)
    {
        link = reverse = null;
        if (Destination is null || !ValidLine(Direction) || !ValidLine(Command, true) ||
            !MapRoomEditorViewModel.Number(Weight, out var weight) || weight < 0 ||
            (CreateReturnExit && !ValidLine(ReturnDirection))) return false;
        link = new(from, Destination.Id, Direction.Trim(), true)
        {
            Command = MapRoomEditorViewModel.Optional(Command), Weight = weight,
            IsLocked = IsLocked, DoorState = Door?.Value ?? MapDoorState.None
        };
        if (CreateReturnExit) reverse = new(Destination.Id, from, ReturnDirection.Trim(), true)
        {
            IsLocked = IsLocked, DoorState = Door?.Value ?? MapDoorState.None
        };
        return true;
    }
    private static bool ValidLine(string text, bool optional = false)
        => (optional || !string.IsNullOrWhiteSpace(text)) && text.Length <= 4096 && !text.Any(char.IsControl);
}
