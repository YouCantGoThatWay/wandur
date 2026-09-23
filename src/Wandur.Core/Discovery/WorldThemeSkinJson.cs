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

        WorldThemeSkinLayout? layout = null;
        if (root.TryGetProperty("layout", out var layoutEl) && layoutEl.ValueKind == JsonValueKind.Object)
            layout = ReadLayout(layoutEl);

        var skin = new WorldThemeSkin
        {
            Version = 1, Window = window, Panels = panels, Layout = layout, Surfaces = ReadSurfaces(root), Radii = ReadRadii(root), Edge = ReadEdge(root),
        };
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
        if (skin.Edge is { } windowEdge)
        {
            writer.WritePropertyName("edge");
            writer.WriteStartObject();
            writer.WriteString("color", windowEdge.Color);
            writer.WriteNumber("thickness", windowEdge.Thickness);
            if (windowEdge.Outline is { Length: > 0 } edgeOutline) writer.WriteString("outline", edgeOutline);
            if (windowEdge.Accent is { Length: > 0 } edgeAccent) writer.WriteString("accent", edgeAccent);
            writer.WriteEndObject();
        }
        if (skin.Radii is { HasContent: true } radii)
        {
            writer.WritePropertyName("radii");
            writer.WriteStartObject();
            if (radii.Panel is { } panelRadius) writer.WriteNumber("panel", panelRadius);
            if (radii.Control is { } controlRadius) writer.WriteNumber("control", controlRadius);
            writer.WriteEndObject();
        }
        if (skin.Surfaces is { HasContent: true } surfaces)
        {
            writer.WritePropertyName("surfaces");
            writer.WriteStartObject();
            void Slot(string name, WorldThemeSkinSurface? surface)
            {
                if (surface is null) return;
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                writer.WriteString("from", surface.From);
                writer.WriteString("to", surface.To);
                if (surface.Gloss > 0) writer.WriteNumber("gloss", surface.Gloss);
                if (surface.Bevel != "none") writer.WriteString("bevel", surface.Bevel);
                if (surface.BevelStrength < 1) writer.WriteNumber("bevel_strength", surface.BevelStrength);
                if (surface.Grain > 0) writer.WriteNumber("grain", surface.Grain);
                if (surface.Rule is { Length: > 0 } rule) writer.WriteString("rule", rule);
                writer.WriteEndObject();
            }
            Slot("titlebar", surfaces.TitleBar);
            Slot("toolbar", surfaces.Toolbar);
            Slot("panel_header", surfaces.PanelHeader);
            Slot("panel_body", surfaces.PanelBody);
            Slot("footer", surfaces.Footer);
            Slot("ground", surfaces.Ground);
            writer.WriteEndObject();
        }
        if (skin.Layout is { } skinLayout && (skinLayout.TitleBar is not null || skinLayout.PanelHeader is not null))
        {
            writer.WritePropertyName("layout");
            writer.WriteStartObject();
            if (skinLayout.PanelHeader is { } panelHeader)
            {
                writer.WritePropertyName("panel_header");
                writer.WriteStartObject();
                writer.WriteNumber("height", panelHeader.Height);
                writer.WritePropertyName("inset");
                WriteBox(writer, panelHeader.Inset);
                writer.WriteEndObject();
            }
            if (skinLayout.TitleBar is not { } titleBar) { writer.WriteEndObject(); }
            else
            {
            writer.WritePropertyName("titlebar");
            writer.WriteStartObject();
            writer.WriteBoolean("hosts_toolbar", titleBar.HostsToolbar);
            writer.WriteString("toolbar_align", titleBar.ToolbarAlign);
            writer.WriteString("title_align", titleBar.TitleAlign);
            if (titleBar.Height is { } barHeight) writer.WriteNumber("height", barHeight);
            if (titleBar.Plaque is { } plaque)
            {
                writer.WritePropertyName("plaque");
                writer.WriteStartObject();
                writer.WriteString("shape", plaque.Shape);
                writer.WriteNumber("cap", plaque.Cap);
                if (plaque.Fill is { Length: > 0 } fill) writer.WriteString("fill", fill);
                if (plaque.Edge is { Length: > 0 } edge) writer.WriteString("edge", edge);
                if (plaque.Accent is { Length: > 0 } accent) writer.WriteString("accent", accent);
                if (plaque.Padding != default)
                {
                    writer.WritePropertyName("padding");
                    WriteBox(writer, plaque.Padding);
                }
                if (plaque.Shadow is { } shadow)
                {
                    writer.WritePropertyName("shadow");
                    writer.WriteStartObject();
                    writer.WriteString("color", shadow.Color);
                    writer.WriteNumber("opacity", shadow.Opacity);
                    writer.WriteNumber("blur", shadow.Blur);
                    writer.WriteNumber("y", shadow.Y);
                    writer.WriteEndObject();
                }
                if (plaque.Wings is { } wings)
                {
                    writer.WritePropertyName("wings");
                    writer.WriteStartObject();
                    writer.WriteNumber("extend", wings.Extend);
                    if (wings.Fill is { Length: > 0 } wingFill) writer.WriteString("fill", wingFill);
                    if (wings.Edge is { Length: > 0 } wingEdge) writer.WriteString("edge", wingEdge);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WritePropertyName("padding");
            WriteBox(writer, titleBar.Padding);
            writer.WriteEndObject();
            writer.WriteEndObject();
            }
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
        // Inset is deliberately NOT required to cover thickness. A nine-slice ties the corner patch size
        // to the slice, so art whose corner fittings run down a rail forces a thick border even when the
        // rail itself is thin, and content would then start far inside a frame that looks slim. The
        // client paints the frame first and arranges content over it, so a smaller inset costs some of
        // the corner art and never costs reachability.
        if (inset.Left < 0 || inset.Top < 0 || inset.Right < 0 || inset.Bottom < 0) return null;

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

    private static readonly HashSet<string> AllowedToolbarAlign = new(StringComparer.Ordinal) { "left", "center", "right" };

    /// <summary>
    /// Slot geometry. The slots themselves are the client's; a theme only says where they sit, and
    /// every number is clamped so a skin cannot push the toolbar out of reach or flatten it away.
    /// </summary>
    private static WorldThemeSkinPanelHeader? ReadPanelHeader(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("height", out var hEl) || !TryGetFiniteNumber(hEl, out var height)) return null;
        if (height is < 18 or > 64) return null;
        var inset = default(SkinBox);
        if (el.TryGetProperty("inset", out _) && !TryReadBox(el, "inset", out inset)) return null;
        if (inset.Left is < 0 or > 128 || inset.Right is < 0 or > 128
            || inset.Top is < 0 or > 128 || inset.Bottom is < 0 or > 128)
            return null;
        return new WorldThemeSkinPanelHeader { Height = height, Inset = inset };
    }

    private static WorldThemeSkinLayout? ReadLayout(JsonElement el)
    {
        WorldThemeSkinPanelHeader? panelHeader = null;
        if (el.TryGetProperty("panel_header", out var panelEl))
        {
            panelHeader = ReadPanelHeader(panelEl);
            if (panelHeader is null) return null;
        }
        if (!el.TryGetProperty("titlebar", out var barEl) || barEl.ValueKind != JsonValueKind.Object)
            return panelHeader is null ? null : new WorldThemeSkinLayout { PanelHeader = panelHeader };
        if (!barEl.TryGetProperty("hosts_toolbar", out var hostsEl)
            || hostsEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        var align = "right";
        if (barEl.TryGetProperty("toolbar_align", out var alignEl))
        {
            if (alignEl.ValueKind != JsonValueKind.String) return null;
            align = alignEl.GetString() ?? "";
            if (!AllowedToolbarAlign.Contains(align)) return null;
        }
        var titleAlign = "center";
        if (barEl.TryGetProperty("title_align", out var titleAlignEl))
        {
            if (titleAlignEl.ValueKind != JsonValueKind.String) return null;
            titleAlign = titleAlignEl.GetString() ?? "";
            if (!AllowedToolbarAlign.Contains(titleAlign)) return null;
        }
        var padding = default(SkinBox);
        if (barEl.TryGetProperty("padding", out _) && !TryReadBox(barEl, "padding", out padding))
            return null;
        if (padding.Left is < 0 or > 400 || padding.Right is < 0 or > 400
            || padding.Top is < 0 or > 96 || padding.Bottom is < 0 or > 96)
            return null;

        double? height = null;
        if (barEl.TryGetProperty("height", out var heightEl))
        {
            if (heightEl.ValueKind != JsonValueKind.Number || !heightEl.TryGetDouble(out var value)) return null;
            if (!double.IsFinite(value) || value is < 28 or > 160) return null;
            height = value;
        }

        // A malformed nameplate costs the title its plate, not the band its layout: the title is drawn
        // straight on the band instead, which is what a theme without a plaque does anyway.
        var plaque = barEl.TryGetProperty("plaque", out var plaqueEl) ? ReadPlaque(plaqueEl) : null;

        return new WorldThemeSkinLayout
        {
            PanelHeader = panelHeader,
            TitleBar = new WorldThemeSkinTitleBar
            {
                HostsToolbar = hostsEl.ValueKind == JsonValueKind.True,
                ToolbarAlign = align,
                TitleAlign = titleAlign,
                Padding = padding,
                Height = height,
                Plaque = plaque
            }
        };
    }

    private static WorldThemePanelSkin? ReadPanel(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!TryReadBorder(el, out var border) || border is null) return null;
        if (!TryReadBox(el, "inset", out var inset)) return null;
        // Same reasoning as the window: a panel's corner fittings are wider than its rails, and tying the
        // body inset to the slice-driven thickness is what cost the dock panels their width.
        if (inset.Left < 0 || inset.Top < 0 || inset.Right < 0 || inset.Bottom < 0) return null;
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

    /// <summary>A sub-rectangle of an overlay, in fractions of its box, fully inside it.</summary>
    private static bool TryReadRect(JsonElement el, out SkinRect rect)
    {
        rect = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("x", out var xEl) || !TryGetFiniteNumber(xEl, out var x)) return false;
        if (!el.TryGetProperty("y", out var yEl) || !TryGetFiniteNumber(yEl, out var y)) return false;
        if (!el.TryGetProperty("width", out var wEl) || !TryGetFiniteNumber(wEl, out var w)) return false;
        if (!el.TryGetProperty("height", out var hEl) || !TryGetFiniteNumber(hEl, out var h)) return false;
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1 || y + h > 1) return false;
        rect = new SkinRect(x, y, w, h);
        return true;
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
        // Bounded so a hostile theme cannot paint over the window; raised for larger frame art.
        if (size.Width is <= 0 or > 1024 || size.Height is <= 0 or > 256) return false;
        SkinRect? textArea = null;
        // Decoration: a malformed inner window costs the title its placement, not the window its
        // plaque, so this is ignored rather than rejecting the whole overlay.
        if (el.TryGetProperty("text_area", out var textEl) && TryReadRect(textEl, out var rect))
            textArea = rect;
        overlay = new WorldThemeSkinOverlay { Id = id, Version = ReadAssetVersion(el), Url = url, Anchor = anchor, Size = size, TextArea = textArea };
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

    /// <summary>
    /// The directory's own label for the bytes behind a url. Opaque to the client: it only has to change when
    /// the asset does. A malformed one is dropped rather than rejecting the asset, which would cost a world
    /// its frame over a caching hint.
    /// </summary>
    private static readonly string[] AllowedBevels = ["raised", "sunken", "none"];

    /// <summary>
    /// A shaded surface is four values and any of them may be wrong without costing the theme its other
    /// slots: a bad gradient drops that one surface, exactly as a bad overlay drops one overlay.
    /// </summary>
    private static WorldThemeSkinSurface? ReadSurface(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("from", out var fromEl) || fromEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("to", out var toEl) || toEl.ValueKind != JsonValueKind.String) return null;
        var from = fromEl.GetString() ?? "";
        var to = toEl.GetString() ?? "";
        if (!WorldTheme.IsColor(from) || !WorldTheme.IsColor(to)) return null;
        var gloss = 0d;
        if (el.TryGetProperty("gloss", out var glossEl))
        {
            if (glossEl.ValueKind != JsonValueKind.Number || !glossEl.TryGetDouble(out gloss)) return null;
            if (!double.IsFinite(gloss) || gloss is < 0 or > 1) return null;
        }
        var bevel = "none";
        if (el.TryGetProperty("bevel", out var bevelEl))
        {
            if (bevelEl.ValueKind != JsonValueKind.String) return null;
            bevel = bevelEl.GetString() ?? "";
            if (!AllowedBevels.Contains(bevel)) return null;
        }
        var bevelStrength = 1d;
        if (el.TryGetProperty("bevel_strength", out var strengthEl))
        {
            if (strengthEl.ValueKind != JsonValueKind.Number || !strengthEl.TryGetDouble(out bevelStrength)) return null;
            if (!double.IsFinite(bevelStrength) || bevelStrength is < 0 or > 1) return null;
        }
        var grain = 0d;
        if (el.TryGetProperty("grain", out var grainEl))
        {
            if (grainEl.ValueKind != JsonValueKind.Number || !grainEl.TryGetDouble(out grain)) return null;
            if (!double.IsFinite(grain) || grain is < 0 or > 1) return null;
        }
        string? rule = null;
        if (el.TryGetProperty("rule", out var ruleEl))
        {
            if (ruleEl.ValueKind != JsonValueKind.String) return null;
            rule = ruleEl.GetString();
            if (!WorldTheme.IsColor(rule)) return null;
        }
        return new WorldThemeSkinSurface { From = from, To = to, Gloss = gloss, Bevel = bevel, BevelStrength = bevelStrength, Grain = grain, Rule = rule };
    }

    private static readonly string[] AllowedPlaqueShapes = ["chamfer", "notch", "round", "square", "fleet"];

    /// <summary>
    /// A nameplate is a shape and up to three colours. A colour the theme leaves out is taken from the
    /// palette rather than guessed, so the smallest useful plaque is a shape on its own.
    /// </summary>
    private static WorldThemeSkinPlaque? ReadPlaque(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var shape = "chamfer";
        if (el.TryGetProperty("shape", out var shapeEl))
        {
            if (shapeEl.ValueKind != JsonValueKind.String) return null;
            shape = shapeEl.GetString() ?? "";
            if (!AllowedPlaqueShapes.Contains(shape)) return null;
        }
        var cap = 6d;
        if (el.TryGetProperty("cap", out var capEl))
        {
            if (capEl.ValueKind != JsonValueKind.Number || !capEl.TryGetDouble(out cap)) return null;
            if (!double.IsFinite(cap) || cap is < 0 or > 32) return null;
        }
        string? Colour(string name)
        {
            if (!el.TryGetProperty(name, out var colourEl) || colourEl.ValueKind != JsonValueKind.String) return null;
            var value = colourEl.GetString();
            return WorldTheme.IsColor(value) ? value : null;
        }
        var padding = default(SkinBox);
        if (el.TryGetProperty("padding", out _) && !TryReadBox(el, "padding", out padding)) return null;
        if (padding.Left is < 0 or > 64 || padding.Right is < 0 or > 64
            || padding.Top is < 0 or > 32 || padding.Bottom is < 0 or > 32) return null;

        WorldThemeSkinShadow? shadow = null;
        if (el.TryGetProperty("shadow", out var shadowEl))
        {
            shadow = ReadShadow(shadowEl);
            if (shadow is null) return null;
        }

        WorldThemeSkinWings? wings = null;
        if (el.TryGetProperty("wings", out var wingsEl))
        {
            wings = ReadWings(wingsEl);
            if (wings is null) return null;
        }

        return new WorldThemeSkinPlaque
        {
            Shape = shape, Cap = cap,
            Fill = Colour("fill"), Edge = Colour("edge"), Accent = Colour("accent"),
            Padding = padding, Shadow = shadow, Wings = wings,
        };
    }

    private static WorldThemeSkinShadow? ReadShadow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("color", out var colourEl) || colourEl.ValueKind != JsonValueKind.String) return null;
        var colour = colourEl.GetString();
        if (!WorldTheme.IsColor(colour)) return null;
        double Number(string name, double fallback, double min, double max, out bool ok)
        {
            ok = true;
            if (!el.TryGetProperty(name, out var node)) return fallback;
            if (node.ValueKind != JsonValueKind.Number || !node.TryGetDouble(out var value)
                || !double.IsFinite(value) || value < min || value > max) { ok = false; return fallback; }
            return value;
        }
        var opacity = Number("opacity", 0.35, 0, 1, out var okOpacity);
        var blur = Number("blur", 8, 0, 24, out var okBlur);
        var y = Number("y", 2, -8, 8, out var okY);
        if (!okOpacity || !okBlur || !okY) return null;
        return new WorldThemeSkinShadow { Color = colour!, Opacity = opacity, Blur = blur, Y = y };
    }

    private static WorldThemeSkinWings? ReadWings(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("extend", out var extendEl) || extendEl.ValueKind != JsonValueKind.Number
            || !extendEl.TryGetDouble(out var extend) || !double.IsFinite(extend) || extend is < 0 or > 160)
            return null;
        string? Colour(string name, out bool ok)
        {
            ok = true;
            if (!el.TryGetProperty(name, out var node)) return null;
            var value = node.ValueKind == JsonValueKind.String ? node.GetString() : null;
            ok = WorldTheme.IsColor(value);
            return ok ? value : null;
        }
        var fill = Colour("fill", out var okFill);
        var edge = Colour("edge", out var okEdge);
        if (!okFill || !okEdge) return null;
        return extend <= 0 ? null : new WorldThemeSkinWings { Extend = extend, Fill = fill, Edge = edge };
    }

    private static WorldThemeSkinEdge? ReadEdge(JsonElement root)
    {
        if (!root.TryGetProperty("edge", out var el) || el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("color", out var colourEl) || colourEl.ValueKind != JsonValueKind.String) return null;
        var colour = colourEl.GetString();
        if (!WorldTheme.IsColor(colour)) return null;
        var thickness = 1d;
        if (el.TryGetProperty("thickness", out var thickEl))
        {
            if (thickEl.ValueKind != JsonValueKind.Number || !thickEl.TryGetDouble(out thickness)) return null;
            if (!double.IsFinite(thickness) || thickness is < 0 or > 16) return null;
        }
        string? outline = null;
        if (el.TryGetProperty("outline", out var outlineEl))
        {
            if (outlineEl.ValueKind != JsonValueKind.String) return null;
            outline = outlineEl.GetString();
            if (!WorldTheme.IsColor(outline)) return null;
        }
        // A bevelled frame needs room for its two outlines and a highlight between them.
        if (outline is not null && thickness < 3) return null;
        string? accent = null;
        if (el.TryGetProperty("accent", out var accentEl) && accentEl.ValueKind == JsonValueKind.String &&
            WorldTheme.IsColor(accentEl.GetString()))
            accent = accentEl.GetString();
        return thickness <= 0 ? null : new WorldThemeSkinEdge
        {
            Color = colour!, Thickness = thickness, Outline = outline, Accent = accent,
        };
    }

    private static WorldThemeSkinRadii? ReadRadii(JsonElement root)
    {
        if (!root.TryGetProperty("radii", out var el) || el.ValueKind != JsonValueKind.Object) return null;
        double? Slot(string name)
        {
            if (!el.TryGetProperty(name, out var slot) || slot.ValueKind != JsonValueKind.Number) return null;
            if (!slot.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 24) return null;
            return value;
        }
        var radii = new WorldThemeSkinRadii { Panel = Slot("panel"), Control = Slot("control") };
        return radii.HasContent ? radii : null;
    }

    private static WorldThemeSkinSurfaces? ReadSurfaces(JsonElement root)
    {
        if (!root.TryGetProperty("surfaces", out var el) || el.ValueKind != JsonValueKind.Object) return null;
        WorldThemeSkinSurface? Slot(string name) =>
            el.TryGetProperty(name, out var slot) ? ReadSurface(slot) : null;
        var surfaces = new WorldThemeSkinSurfaces
        {
            TitleBar = Slot("titlebar"),
            Toolbar = Slot("toolbar"),
            PanelHeader = Slot("panel_header"),
            PanelBody = Slot("panel_body"),
            Footer = Slot("footer"),
            Ground = Slot("ground"),
        };
        return surfaces.HasContent ? surfaces : null;
    }

    private static string? ReadAssetVersion(JsonElement el) =>
        el.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.String &&
        versionEl.GetString() is { Length: > 0 and <= 128 } version &&
        version.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? version : null;

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
        var repeat = "stretch";
        if (el.TryGetProperty("repeat", out var repeatEl))
        {
            if (repeatEl.ValueKind != JsonValueKind.String) return false;
            repeat = repeatEl.GetString() ?? "";
            if (repeat is not ("stretch" or "tile")) return false;
        }
        border = new WorldThemeSkinBorder { Url = url, Version = ReadAssetVersion(el), SourceSize = source, Slice = slice, Thickness = thickness, Repeat = repeat };
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
        if (left is < 0 or > 256 || top is < 0 or > 256 || right is < 0 or > 256 || bottom is < 0 or > 256)
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
        t.Left is >= 0 and <= 256 && t.Top is >= 0 and <= 256 &&
        t.Right is >= 0 and <= 256 && t.Bottom is >= 0 and <= 256;

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
                if (overlay.Version is { Length: > 0 } overlayVersion) writer.WriteString("version", overlayVersion);
                writer.WriteString("anchor", overlay.Anchor);
                writer.WritePropertyName("size");
                WriteSize(writer, overlay.Size);
                if (overlay.TextArea is { } textArea)
                {
                    // Round-tripping a theme through the directory must not lose the plaque's inner
                    // window, or the title goes back to being centred across the metal shoulders.
                    writer.WritePropertyName("text_area");
                    writer.WriteStartObject();
                    writer.WriteNumber("x", textArea.X);
                    writer.WriteNumber("y", textArea.Y);
                    writer.WriteNumber("width", textArea.Width);
                    writer.WriteNumber("height", textArea.Height);
                    writer.WriteEndObject();
                }
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
        if (border.Version is { Length: > 0 } borderVersion) writer.WriteString("version", borderVersion);
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
        if (border.Tiles) writer.WriteString("repeat", border.Repeat);
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
