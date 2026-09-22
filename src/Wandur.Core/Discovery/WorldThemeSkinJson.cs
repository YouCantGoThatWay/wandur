using System.Security.Cryptography;
using System.Text.Json;

namespace Wandur.Core.Discovery;

/// <summary>Strict reader/writer for optional theme.skin. Malformed sections are dropped, never fail the palette.</summary>
public static class WorldThemeSkinJson
{
    private static readonly HashSet<string> AllowedAnchors = ["top-center", "bottom-left", "bottom-right"];

    public static WorldThemeSkin? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.Number ||
            !versionEl.TryGetInt32(out var version) || version != 1)
            return null;

        WorldThemeWindowSkin? window = null;
        if (root.TryGetProperty("window", out var windowEl))
            window = ReadWindow(windowEl);

        WorldThemePanelStyles? panels = null;
        if (root.TryGetProperty("panels", out var panelsEl) && panelsEl.ValueKind == JsonValueKind.Object)
        {
            WorldThemePanelSkin? def = null;
            if (panelsEl.TryGetProperty("default", out var defaultEl))
                def = ReadPanel(defaultEl);
            if (def is not null) panels = new WorldThemePanelStyles { Default = def };
        }

        var skin = new WorldThemeSkin { Version = 1, Window = window, Panels = panels };
        return skin.HasContent ? skin : null;
    }

    public static void Write(Utf8JsonWriter writer, WorldThemeSkin skin)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        if (skin.Window is { } window)
        {
            writer.WritePropertyName("window");
            WriteWindow(writer, window);
        }
        if (skin.Panels?.Default is { } panel)
        {
            writer.WritePropertyName("panels");
            writer.WriteStartObject();
            writer.WritePropertyName("default");
            WritePanel(writer, panel);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    /// <summary>Deterministic known-field JSON hash for structural comparison across deserializations.</summary>
    public static string CanonicalKey(WorldThemeSkin skin)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            Write(writer, skin);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static WorldThemeWindowSkin? ReadWindow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!TryReadBorder(el, out var border) || border is null) return null;
        if (!TryReadBox(el, "inset", out var inset)) return null;
        if (!InsetCoversThickness(inset, border.Thickness)) return null;

        var overlays = ReadOverlays(el, inset, out var footer);
        SkinSize? compact = null;
        if (el.TryGetProperty("compact_below", out var compactEl))
        {
            if (!TryReadSize(compactEl, out var size) ||
                size.Width is < 640 or > 2560 || size.Height is < 480 or > 1600)
                return null;
            compact = size;
        }

        return new WorldThemeWindowSkin
        {
            Border = border,
            Inset = inset,
            Overlays = overlays,
            FooterClearance = footer,
            CompactBelow = compact
        };
    }

    private static WorldThemePanelSkin? ReadPanel(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!TryReadBorder(el, out var border) || border is null) return null;
        if (!TryReadBox(el, "inset", out var inset)) return null;
        if (inset.Left < border.Thickness.Left || inset.Right < border.Thickness.Right ||
            inset.Bottom < border.Thickness.Bottom)
            return null;
        if (!el.TryGetProperty("header_height", out var hhEl) || !TryGetFiniteNumber(hhEl, out var headerHeight) ||
            headerHeight is < 24 or > 48)
            return null;
        if (inset.Top + headerHeight < border.Thickness.Top) return null;
        return new WorldThemePanelSkin { Border = border, Inset = inset, HeaderHeight = headerHeight };
    }

    private static IReadOnlyList<WorldThemeSkinOverlay> ReadOverlays(
        JsonElement window, SkinBox inset, out SkinFooterClearance footer)
    {
        footer = default;
        if (window.TryGetProperty("footer_clearance", out var footerEl))
        {
            if (!TryReadFooter(footerEl, out footer))
            {
                // Bad footer object: keep window border but drop overlays that need clearance.
                footer = default;
            }
        }

        if (!window.TryGetProperty("overlays", out var overlaysEl))
            return Array.Empty<WorldThemeSkinOverlay>();
        if (overlaysEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<WorldThemeSkinOverlay>();
        if (overlaysEl.GetArrayLength() > 3)
            return Array.Empty<WorldThemeSkinOverlay>();

        var accepted = new List<WorldThemeSkinOverlay>(3);
        var seenAnchors = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in overlaysEl.EnumerateArray())
        {
            if (!TryReadOverlay(item, out var overlay) || overlay is null) continue;
            if (!seenAnchors.Add(overlay.Anchor) || !seenIds.Add(overlay.Id)) continue;
            if (!OverlayFits(overlay, inset, footer)) continue;
            accepted.Add(overlay);
        }
        return accepted.Count == 0 ? Array.Empty<WorldThemeSkinOverlay>() : accepted.ToArray();
    }

    private static bool TryReadOverlay(JsonElement el, out WorldThemeSkinOverlay? overlay)
    {
        overlay = null;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
        var id = idEl.GetString() ?? "";
        if (id.Length is 0 or > 40 || !id.All(c => c is >= (char)0x20 and <= (char)0x7E)) return false;
        if (!el.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return false;
        var url = urlEl.GetString() ?? "";
        if (!WorldTheme.IsThemeUrl(url)) return false;
        if (!el.TryGetProperty("anchor", out var anchorEl) || anchorEl.ValueKind != JsonValueKind.String) return false;
        var anchor = anchorEl.GetString() ?? "";
        if (!AllowedAnchors.Contains(anchor)) return false;
        if (!el.TryGetProperty("size", out var sizeEl) || !TryReadSize(sizeEl, out var size)) return false;
        if (size.Width is <= 0 or > 512 || size.Height is <= 0 or > 128) return false;
        overlay = new WorldThemeSkinOverlay { Id = id, Url = url, Anchor = anchor, Size = size };
        return true;
    }

    private static bool OverlayFits(WorldThemeSkinOverlay overlay, SkinBox inset, SkinFooterClearance footer)
    {
        // Top-center ornaments may extend below inset.top into chrome (titlebar). Measured
        // industrial assets use a 64 DIP header over a 38 DIP top inset; live controls stay below.
        if (overlay.Anchor == "top-center")
            return overlay.Size.Height > 0;

        // Bottom overlays: height must fit in inset.bottom + footer.min_height.
        if (overlay.Size.Height > inset.Bottom + footer.MinHeight) return false;
        var intrudes = overlay.Size.Height > inset.Bottom;
        if (!intrudes) return true;
        var sideInset = overlay.Anchor == "bottom-left" ? inset.Left : inset.Right;
        var required = Math.Max(0, overlay.Size.Width - sideInset) + 4;
        var clearance = overlay.Anchor == "bottom-left" ? footer.Left : footer.Right;
        return clearance >= required;
    }

    private static bool TryReadBorder(JsonElement parent, out WorldThemeSkinBorder? border)
    {
        border = null;
        if (!parent.TryGetProperty("border", out var el) || el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return false;
        var url = urlEl.GetString() ?? "";
        if (!WorldTheme.IsThemeUrl(url)) return false;
        if (!el.TryGetProperty("source_size", out var sizeEl) || !TryReadPixelSize(sizeEl, out var source)) return false;
        if (source.Width is < 1 or > 4096 || source.Height is < 1 or > 4096) return false;
        if ((long)source.Width * source.Height > 8_388_608) return false;
        if (!el.TryGetProperty("slice", out var sliceEl) || !TryReadPixelBox(sliceEl, out var slice)) return false;
        if (slice.Left < 0 || slice.Top < 0 || slice.Right < 0 || slice.Bottom < 0) return false;
        if (slice.Left > 4096 || slice.Top > 4096 || slice.Right > 4096 || slice.Bottom > 4096) return false;
        if (slice.Left + slice.Right >= source.Width || slice.Top + slice.Bottom >= source.Height) return false;
        if (!el.TryGetProperty("thickness", out var thickEl) || !TryReadBox(thickEl, out var thickness)) return false;
        if (!ThicknessInRange(thickness)) return false;
        border = new WorldThemeSkinBorder { Url = url, SourceSize = source, Slice = slice, Thickness = thickness };
        return true;
    }

    private static bool TryReadBox(JsonElement parent, string name, out SkinBox box)
    {
        box = default;
        if (!parent.TryGetProperty(name, out var el)) return false;
        return TryReadBox(el, out box);
    }

    private static bool TryReadBox(JsonElement el, out SkinBox box)
    {
        box = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetFiniteNumber(el, "left", out var left) || !TryGetFiniteNumber(el, "top", out var top) ||
            !TryGetFiniteNumber(el, "right", out var right) || !TryGetFiniteNumber(el, "bottom", out var bottom))
            return false;
        if (left is < 0 or > 128 || top is < 0 or > 128 || right is < 0 or > 128 || bottom is < 0 or > 128)
            return false;
        box = new SkinBox(left, top, right, bottom);
        return true;
    }

    private static bool TryReadFooter(JsonElement el, out SkinFooterClearance footer)
    {
        footer = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetFiniteNumber(el, "left", out var left) || !TryGetFiniteNumber(el, "right", out var right) ||
            !TryGetFiniteNumber(el, "min_height", out var minHeight))
            return false;
        if (left is < 0 or > 160 || right is < 0 or > 160 || minHeight is < 0 or > 48) return false;
        footer = new SkinFooterClearance(left, right, minHeight);
        return true;
    }

    private static bool TryReadSize(JsonElement el, out SkinSize size)
    {
        size = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetFiniteNumber(el, "width", out var width) || !TryGetFiniteNumber(el, "height", out var height))
            return false;
        size = new SkinSize(width, height);
        return true;
    }

    private static bool TryReadPixelSize(JsonElement el, out SkinPixelSize size)
    {
        size = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetInt(el, "width", out var width) || !TryGetInt(el, "height", out var height)) return false;
        size = new SkinPixelSize(width, height);
        return true;
    }

    private static bool TryReadPixelBox(JsonElement el, out SkinPixelBox box)
    {
        box = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetInt(el, "left", out var left) || !TryGetInt(el, "top", out var top) ||
            !TryGetInt(el, "right", out var right) || !TryGetInt(el, "bottom", out var bottom))
            return false;
        box = new SkinPixelBox(left, top, right, bottom);
        return true;
    }

    private static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return false;
        // Reject numeric strings and non-integers: GetInt32 fails on fractions.
        if (!el.TryGetInt32(out value)) return false;
        // Ensure it was written as an integer (no fractional part).
        if (el.TryGetDouble(out var d) && d != value) return false;
        return true;
    }

    private static bool TryGetFiniteNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var el)) return false;
        return TryGetFiniteNumber(el, out value);
    }

    private static bool TryGetFiniteNumber(JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Number) return false;
        if (!el.TryGetDouble(out value) || !double.IsFinite(value)) return false;
        return true;
    }

    private static bool ThicknessInRange(SkinBox t) =>
        t.Left is >= 0 and <= 128 && t.Top is >= 0 and <= 128 &&
        t.Right is >= 0 and <= 128 && t.Bottom is >= 0 and <= 128;

    private static bool InsetCoversThickness(SkinBox inset, SkinBox thickness) =>
        inset.Left >= thickness.Left && inset.Top >= thickness.Top &&
        inset.Right >= thickness.Right && inset.Bottom >= thickness.Bottom;

    private static void WriteWindow(Utf8JsonWriter writer, WorldThemeWindowSkin window)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("border");
        WriteBorder(writer, window.Border);
        writer.WritePropertyName("inset");
        WriteBox(writer, window.Inset);
        if (window.Overlays.Count > 0)
        {
            writer.WritePropertyName("overlays");
            writer.WriteStartArray();
            foreach (var overlay in window.Overlays)
            {
                writer.WriteStartObject();
                writer.WriteString("id", overlay.Id);
                writer.WriteString("url", overlay.Url);
                writer.WriteString("anchor", overlay.Anchor);
                writer.WritePropertyName("size");
                WriteSize(writer, overlay.Size);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (window.FooterClearance != default)
        {
            writer.WritePropertyName("footer_clearance");
            writer.WriteStartObject();
            writer.WriteNumber("left", window.FooterClearance.Left);
            writer.WriteNumber("right", window.FooterClearance.Right);
            writer.WriteNumber("min_height", window.FooterClearance.MinHeight);
            writer.WriteEndObject();
        }
        if (window.CompactBelow is { } compact)
        {
            writer.WritePropertyName("compact_below");
            WriteSize(writer, compact);
        }
        writer.WriteEndObject();
    }

    private static void WritePanel(Utf8JsonWriter writer, WorldThemePanelSkin panel)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("border");
        WriteBorder(writer, panel.Border);
        writer.WritePropertyName("inset");
        WriteBox(writer, panel.Inset);
        writer.WriteNumber("header_height", panel.HeaderHeight);
        writer.WriteEndObject();
    }

    private static void WriteBorder(Utf8JsonWriter writer, WorldThemeSkinBorder border)
    {
        writer.WriteStartObject();
        writer.WriteString("url", border.Url);
        writer.WritePropertyName("source_size");
        writer.WriteStartObject();
        writer.WriteNumber("width", border.SourceSize.Width);
        writer.WriteNumber("height", border.SourceSize.Height);
        writer.WriteEndObject();
        writer.WritePropertyName("slice");
        writer.WriteStartObject();
        writer.WriteNumber("left", border.Slice.Left);
        writer.WriteNumber("top", border.Slice.Top);
        writer.WriteNumber("right", border.Slice.Right);
        writer.WriteNumber("bottom", border.Slice.Bottom);
        writer.WriteEndObject();
        writer.WritePropertyName("thickness");
        WriteBox(writer, border.Thickness);
        writer.WriteEndObject();
    }

    private static void WriteBox(Utf8JsonWriter writer, SkinBox box)
    {
        writer.WriteStartObject();
        writer.WriteNumber("left", box.Left);
        writer.WriteNumber("top", box.Top);
        writer.WriteNumber("right", box.Right);
        writer.WriteNumber("bottom", box.Bottom);
        writer.WriteEndObject();
    }

    private static void WriteSize(Utf8JsonWriter writer, SkinSize size)
    {
        writer.WriteStartObject();
        writer.WriteNumber("width", size.Width);
        writer.WriteNumber("height", size.Height);
        writer.WriteEndObject();
    }
}
