using Wandur.Core.Mapping;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed record MapEnvironmentStyle(string Key, string Label, string Color, string Symbol);

/// <summary>Presentation palette; explicit room metadata is never inferred from prose here.</summary>
public static class MapEnvironmentPalette
{
    public static IReadOnlyList<MapEnvironmentStyle> Styles =>
    [
        new("unknown", L.MapTerrainUnknown, "#77858E", ""),
        new("indoor", L.MapTerrainIndoor, "#D9CBB0", ""),
        new("city", L.MapTerrainCity, "#AC8990", ""),
        new("road", L.MapTerrainRoad, "#AC851E", ""),
        new("grassland", L.MapTerrainGrassland, "#A0C536", ""),
        new("forest", L.MapTerrainForest, "#2E803F", ""),
        new("desert", L.MapTerrainDesert, "#E8CD79", ""),
        new("mountain", L.MapTerrainMountain, "#96988A", "△"),
        new("cave", L.MapTerrainCave, "#726D87", ""),
        new("water", L.MapTerrainWater, "#16A7D5", ""),
        new("underwater", L.MapTerrainUnderwater, "#326AB5", ""),
        new("swamp", L.MapTerrainSwamp, "#649888", ""),
        new("snow", L.MapTerrainSnow, "#C8E1E7", ""),
        new("spacecraft", L.MapTerrainSpacecraft, "#657A88", "")
    ];

    public static string LabelKey(string key) => key switch
    {
        "unknown" => nameof(L.MapTerrainUnknown),
        "indoor" => nameof(L.MapTerrainIndoor),
        "city" => nameof(L.MapTerrainCity),
        "road" => nameof(L.MapTerrainRoad),
        "grassland" => nameof(L.MapTerrainGrassland),
        "forest" => nameof(L.MapTerrainForest),
        "desert" => nameof(L.MapTerrainDesert),
        "mountain" => nameof(L.MapTerrainMountain),
        "cave" => nameof(L.MapTerrainCave),
        "water" => nameof(L.MapTerrainWater),
        "underwater" => nameof(L.MapTerrainUnderwater),
        "swamp" => nameof(L.MapTerrainSwamp),
        "snow" => nameof(L.MapTerrainSnow),
        "spacecraft" => nameof(L.MapTerrainSpacecraft),
        _ => nameof(L.MapTerrainUnknown)
    };

    public static string LabelFor(string? environment) =>
        Styles.FirstOrDefault(s => s.Key == Normalize(environment))?.Label ?? environment ?? L.MapTerrainUnknown;

    public static MapEnvironmentStyle Resolve(MapRoom room, IReadOnlyList<MapEnvironmentStyle>? palette = null)
    {
        palette ??= Styles;
        var style = palette.FirstOrDefault(s => s.Key == Normalize(room.Environment)) ?? palette[0];
        return style with { Color = room.Color ?? style.Color, Symbol = room.Symbol ?? style.Symbol };
    }

    private static string Normalize(string? environment) => environment?.Trim().ToLowerInvariant() switch
    {
        "inside" or "indoors" or "building" => "indoor",
        "town" or "settlement" or "urban" => "city",
        "path" or "street" or "trail" => "road",
        "field" or "fields" or "plains" or "plain" or "grass" => "grassland",
        "woods" or "woodland" or "jungle" => "forest",
        "sand" or "beach" => "desert",
        "mountains" or "hills" or "hill" => "mountain",
        "underground" or "cavern" => "cave",
        "river" or "ocean" or "sea" or "lake" or "water_swim" or "water_noswim" => "water",
        "marsh" or "wetland" => "swamp",
        "ice" or "tundra" => "snow",
        "spaceship" or "space station" or "starship" => "spacecraft",
        var value => value ?? "unknown"
    };
}
