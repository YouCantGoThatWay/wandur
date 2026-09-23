# Image-free Fleet skin implementation plan and Claude handoff

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task when those skills are available. Steps use checkbox syntax for tracking. This document is the handoff, not evidence that the implementation is complete.

**Goal:** Reproduce the approved pale hull-gray macOS/Windows Wandur reference with code-drawn chrome, an expandable centered title plaque, dark MUD working surfaces, and fully functional optional docking panels.

**Architecture:** Refine the existing drawing and theme pipeline, not a second skin system. Avalonia draws the frame, plaque, gradients, highlights and cyan rails; real controls render text and handle input. The client owns geometry and native integration; directory JSON supplies bounded theme values, never executable XAML or arbitrary drawing commands.

**Tech Stack:** Existing .NET 10, Avalonia 12.1.2, Dock.Avalonia 12.1.0.6, code-first C# views, existing AXAML control styles and xUnit/headless tests. No new UI library or rendering engine.

**Spec:** This handoff is the implementation-specific supplement to `/Volumes/Extreme SSD/workspace/wandur/assets/wandur-naval-theme-proposal/MACOS-AND-EXPANDABLE-TITLE.md` and `REVISION-V2.md` in that directory. The visual target is `concept-macos.png`; `concept-v2.png` is the Windows reference. This document overrides older bitmap-slicing instructions and resolves the metrics below. The reference images are not runtime assets.

## Global constraints

- Workspace root: `/Volumes/Extreme SSD/workspace/wandur`. Client and site are separate repositories underneath it. Paths below are relative to that workspace unless stated otherwise.
- Read root `CLAUDE.md`, client `CLAUDE_HANDOFF.md`, and site `HANDOFF.md` before work in their respective repositories.
- Both repositories contain extensive uncommitted work, including the current image-free theme implementation. Inspect diffs, preserve it, and make surgical changes. Do not reset, overwrite, broadly stage, push or deploy it.
- Do not read credentials, dump profile databases, or restart the owner's live MUD sessions. Screenshots use isolated test settings and synthetic content.
- This is a visual implementation/refinement, not a terminal rewrite, docking rewrite, artwork-generation task, or redesign of the directory website.
- No skin PNGs, SVG image files, ImageBrush textures, bitmap caching, screenshots behind controls, Viewbox scaling, procedural scratches or noise. Existing app logos and world thumbnails are unrelated and can remain images.
- Keep legacy image-skin support for other themes. Bypass it for this theme; do not delete its infrastructure or assets.
- Scope hull-gray color changes to the Hull/Fleet look. Preserve other presets, custom user colors, accessibility overrides and existing world-theme precedence. Do not force every world into this palette.
- World name in the plaque is live session data. Never hardcode LEGENDS OF THE JEDI in production, and never bind the plaque to an unconnected world-picker selection.
- Only plain geometric naval styling. No franchise emblems, movie fonts, ship silhouettes, fictional equipment markings or weathered armor plates.

## 1. What we are building, precisely

It is one continuous window chassis with slim, ordinary dock headers inside it. The visual character comes from a pale metal face, a narrow bevel, a shallow stepped title bracket, a dark rectangular inset and small cyan lights. It is not an arrangement of separate picture frames.

```text
Window decorations and client chrome, in one coordinated coordinate system
  56 DIP title band: native controls + full-window-centered title plaque
  40 DIP application toolbar: existing controls, no duplicate brand/title row
  optional existing notice/menu rows, only when actually needed
  flexible docking workspace: optional left | expanding center | optional right
  24 DIP status/footer, with existing content and responsive overflow
  6 DIP outer side/bottom frame with continuous cyan side rails
```

The main title band has its own reserved space exactly once. The application toolbar is a separate intentional row below it, not part of its drag overlay. Preserve a required Windows application menu; do not silently delete commands to match a picture. The reference's title bracket overlaps its toolbar slightly: do not reproduce that artifact. All plaque paint stays inside the 56 DIP title band.

### Dimensions and surfaces

These are DIP, not screenshot pixels. They are initial acceptance targets, not values to scale with the entire window.

| Part | Target |
| --- | --- |
| Main title band | 56 high |
| Plaque host | 44 high, centered vertically within band |
| Plaque preferred minimum width | 480, clamped down to available safe width |
| Plaque preferred maximum width | 640, also clamped to available safe width |
| Fixed cap region | 32 per end |
| Title inset from whole plaque bounds | 60 per end, includes cap, lamp and text clearance |
| Plaque text | Inter 15, semibold, letter spacing 1.5, one line |
| Toolbar | 40 high |
| Dock header | 30 high, live label and 24 by 24 controls |
| Outer side/bottom frame | 6 thick |
| Side cyan line | 1.5 thick inside each outer rail |
| Dock separation / splitter | preserve current functional splitter; visually about 4 to 5 |
| Panel corner radius | 2; control radius 3; OS owns actual outer window rounding |
| Composer row | about 42; readable 14 DIP monospace text |
| Footer | target 24, do not clip localized or accessibility-scaled content |

| Role | Color |
| --- | --- |
| Lit metal edge | `#E7E7E0` |
| Hull face | `#D0D0CA` |
| Lower bevel | `#A5A7A3` |
| Structural seam | `#424743` |
| Chrome foreground / secondary | `#202622` / `#49534D` |
| Workspace body | `#D8DAD5` |
| Terminal, map and channel transcript | `#11171B` |
| Dark raised surface and plaque | `#20292D` |
| Dark foreground / secondary | `#DCE6EE` / `#8DA5B6` |
| Structural cyan / active indicator | `#8DDEE5` / `#35D3E8` |

Use a smooth vertical metal gradient with a narrow top highlight and bottom shadow. The earlier preferred stops were `#E7E7E0` at 0%, `#D9DAD4` at 12%, `#D0D0CA` at 85%, `#A5A7A3` at 100%. It is acceptable to approximate this with existing surface endpoints plus explicit thin bevel strokes. Do not expand the schema into a general gradient editor just to represent those stops. Grain and Gloss are zero for this look. Do not paint the entire dock body with the shiny header gradient.

Bright cyan is for lamps, active indicators and focus, not small text on pale chrome. Preserve legible foreground/background pairs for buttons, selections, disabled controls and ANSI output. Keep the existing dark-compatible ANSI palette.

## 2. Verified current code and specific differences to resolve

Inspection on 2026-09-23 found that the application is already partly code-drawn. These are code observations, not a claim that a current native screenshot has been tested:

| Existing file | Current responsibility and required change |
| --- | --- |
| `wandur-client/src/Wandur.Desktop/DefaultSkin.cs` | Supplies fallback geometry for all palettes. `For` currently chooses a 52 DIP band and `chamfer` plaque with 46 DIP wings. Keep its palette-aware behavior; select the Fleet geometry deliberately without breaking explicit world overrides. |
| `wandur-client/src/Wandur.Desktop/ThemePlaque.cs` | Draws the plaque. `Outline("chamfer")` makes a pointed six-sided shape; the same silhouette is reused for both outer bracket and dark plate. The target needs two different silhouettes: shallow stepped outer shoulders and a rectangular dark inset. |
| `wandur-client/src/Wandur.Desktop/MainWindow.cs` | Builds title/toolbar, moves title into plaque, integrates dragging and updates live title. `ApplyTitleChrome` currently constrains plaque width with `size.Width * 0.36`, `size.Width * 0.5` and a 320 minimum. Replace Fleet sizing with measured text and symmetric native exclusions. Keep the legacy path for bitmap skins. |
| `wandur-client/src/Wandur.Desktop/ThemeWindowSkinHost.cs` | Already reserves and paints an image-free band and bevel. `ActiveInset` handles BandHeight when BorderBitmap is null, but public `Inset` is reset to default in that case. Do not use bitmap-only `Inset.Top` as the authoritative native title height. Add continuous drawn side accents here. |
| `wandur-client/src/Wandur.Desktop/ThemeSkinSurfaces.cs` | Maps surface slots to live brushes. Its bevel/rule stops are proportions of surface height. For Fleet's fixed-width rails and bevel strokes use real geometry, not proportional stops on a tall dock body. Disable grain/gloss for Fleet. |
| `wandur-client/src/Wandur.Desktop/ThemeMaterials.cs` | Older metallic/image overlay path runs before surface overrides. Ensure Fleet takes a clean no-texture path; its appearance must not rely on this method's generated grain or downloaded textures. |
| `wandur-client/src/Wandur.Desktop/ThemeService.cs` | Resolves palette, merges DefaultSkin, applies materials then skin surfaces. Make the Hull/Fleet appearance deterministic here, retain user/world precedence and complete restoration when switching themes. |
| `wandur-client/src/Wandur.Core/Settings/UserTheme.cs` | Contains the existing Hull preset. Adjust it, do not add a duplicate near-identical preset. Use terminal-specific foregrounds rather than the light chrome foreground on dark inputs. |
| `wandur-client/src/Wandur.Desktop/App.axaml` | Global controls and dock styles. Style live dock headers/controls here or in the existing style include, scoped to the resolved theme. |
| `wandur-client/src/Wandur.Desktop/Styles/ThemeDockSkin.axaml` | Retains Dock PARTs but its special selectors are gated by `IsSkinActive=True`, currently meaning a bitmap panel is ready. Do not depend on those selectors to activate code-drawn Fleet headers. |
| `wandur-client/src/Wandur.Desktop/ThemeDockSkinHost.cs` | Legacy bitmap panel wrapper plus radius handling. Without art it should remain a passthrough. Never set bitmap-ready state merely to get the new header styles. |
| `wandur-client/src/Wandur.Desktop/ThemeSkinResources.cs` | A view of ready bitmaps. `FromApplied()` returns null without images. Image-free layout must come from `ThemeService.AppliedSkin`, not this bitmap-ready object. |

Read the current diffs before editing any of these. Existing implementations may advance after this handoff.

## Task 1: Make title layout deterministic and testable

**Create:** `wandur-client/src/Wandur.Desktop/FleetTitleLayout.cs` and `wandur-client/tests/Wandur.Desktop.Tests/FleetTitleLayoutTests.cs`.

**Modify:** `MainWindow.cs`, specifically the Fleet branch of `ApplyTitleChrome`, title updates, size/state updates and native-safe-area integration. This is a small layout helper, not a new theme service.

**Interface:** A pure `FleetTitleLayout.Calculate(windowWidth, leftExclusion, rightExclusion, measuredTextWidth)` returns the title module Rect and whether to fall back to a plain title. Window width and exclusions are in the same client-chrome coordinate space. Exclusions include actual caption-control bounds; do not assume the macOS and Windows values are equal.

- [ ] Add tests first using the exact cases below, run them and record the expected missing-helper failure.
- [ ] Implement the calculation below. It defines the intended layout contract; adapt namespace/import syntax only.

```csharp
using Avalonia;
namespace Wandur.Desktop;

internal readonly record struct FleetTitlePlacement(Rect Bounds, bool PlainTitle);

internal static class FleetTitleLayout
{
    public const double BandHeight = 56;
    public const double PlaqueHeight = 44;
    public const double CapWidth = 32;
    public const double TextInset = 60;

    public static FleetTitlePlacement Calculate(
        double windowWidth, double leftExclusion, double rightExclusion,
        double measuredTextWidth)
    {
        static double Safe(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
        var w = Safe(windowWidth);
        var exclusion = Math.Max(Safe(leftExclusion), Safe(rightExclusion)) + 12;
        var available = Math.Max(0, w - 2 * exclusion);
        var desired = Math.Clamp(Safe(measuredTextWidth) + 2 * TextInset, 480, 640);
        var width = Math.Min(desired, available);
        var plain = width < 2 * TextInset + 48;
        return new(new Rect((w - width) / 2, 6, width, PlaqueHeight), plain);
    }
}
```

```csharp
using Wandur.Desktop;
namespace Wandur.Desktop.Tests;

public sealed class FleetTitleLayoutTests
{
    [Theory]
    [InlineData(1380, 88, 0, 280, 450, 480, false)]
    [InlineData(1380, 0, 140, 600, 370, 640, false)]
    [InlineData(600, 88, 0, 600, 100, 400, false)]
    [InlineData(300, 88, 0, 280, 100, 100, true)]
    public void CentersAndClamps(double w, double left, double right, double text,
        double expectedX, double expectedWidth, bool plain)
    {
        var result = FleetTitleLayout.Calculate(w, left, right, text);
        Assert.Equal(expectedX, result.Bounds.X, 3);
        Assert.Equal(expectedWidth, result.Bounds.Width, 3);
        Assert.Equal(6, result.Bounds.Y);
        Assert.Equal(44, result.Bounds.Height);
        Assert.Equal(w / 2, result.Bounds.Center.X, 3);
        Assert.Equal(plain, result.PlainTitle);
    }
}
```

- [ ] Measure the actual rendered title with its configured font, size, weight and letter spacing. Do not estimate with character count. Constrain its final text box to `moduleWidth - 120`, ellipsize, and expose full text through tooltip and accessibility name. Plain-title fallback uses the safe span without decorative caps; zero space means no painted title.
- [ ] Position the plaque relative to the full window chrome width, independent of left/right dock widths. Recompute on title, font, width, scale, native-decoration metrics and window-state changes, not on every terminal output line. Avoid reentrant layout changes.
- [ ] Preserve `UpdateStatus`/existing active-session title behavior: plaque shows world name or WANDUR, OS title retains character/world/app information. Long names must not move native controls or change the band height.
- [ ] Remove the existing percentage-width rules only from this new geometry path. Rerun the new tests and the existing TitleBarTests.

Run from `wandur-client`: `dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Release --filter 'FullyQualifiedName~FleetTitleLayoutTests|FullyQualifiedName~TitleBarTests'`.

## Task 2: Draw the correct plaque, not two nested hexagons

**Modify:** `ThemePlaque.cs`; bounded shape contract in `WorldThemeSkin.cs` and `WorldThemeSkinJson.cs`. Site contract matching is in Task 5. **Test:** new `FleetPlaqueGeometryTests.cs` beside the existing Desktop tests, plus Core `WorldThemePlaqueTests.cs`.

**Interface:** Add one recognized shape value, `fleet`, without changing `chamfer`, `notch`, `round` or `square` for existing themes. Fleet has a fixed-cap outer bracket and a separate inset rectangle. Geometry stays client-owned.

- [ ] Add geometry tests before the drawing branch: widths 240, 480 and 640 at height 44; outer shape stays inside bounds, plate stays within bracket, lamps stay outside the text area, and left/right cap measurements stay identical when width changes.
- [ ] Implement a Fleet-only outer geometry from these DIP points for width `w`, height `h`. Test this geometry through an internal helper instead of duplicating coordinates in tests. Skip decoration when the layout helper selects the plain fallback.

```csharp
// Local coordinates. Only the right-hand x coordinates depend on w.
Point[] outline =
[
    new(0, 10), new(22, 10), new(30, 2), new(w - 30, 2),
    new(w - 22, 10), new(w, 10), new(w, h - 8),
    new(w - 22, h - 8), new(w - 30, h - 2), new(30, h - 2),
    new(22, h - 8), new(0, h - 8)
];
// Draw with StreamGeometry. The inset is NOT this polygon again.
var plate = new Rect(32, 6, Math.Max(0, w - 64), Math.Max(0, h - 12));
var leftLamp = new Rect(plate.X + 10, plate.Y + 6, 3, Math.Max(0, plate.Height - 12));
var rightLamp = new Rect(plate.Right - 13, plate.Y + 6, 3, Math.Max(0, plate.Height - 12));
```

- [ ] Paint order: light outer bracket, restrained dark outline, top highlight, dark inset with radius 2, thin inset rim, cyan lamps, then the live child text. Text padding is 60 per side total, not old padding plus Wings. Cache reusable brushes/geometries when size/theme changes rather than allocating on every render.
- [ ] Use the existing Fill/Edge/Accent/Wings color fields where appropriate; in Fleet mode the shape owns the 32 DIP end geometry. Do not let legacy Wings.Extend add a second 46 DIP inset. Document that Fleet ignores legacy wing-extend sizing. Preserve existing shapes' behavior.
- [ ] Omit the current multiple-inflated-polygons shadow for Fleet, or replace it with a subtle contained 1 DIP lower rim. The module must not paint into the toolbar. No large blurred glow or shadow is needed.
- [ ] Rerun geometry, layout, Core plaque and existing skin tests, then render a plaque preview at the three widths. Visually inspect it before proceeding: dark inset should be rectangular, shoulders shallow, and caps identical.

## Task 3: Make the shell continuous and surfaces consistent

**Modify:** `DefaultSkin.cs`, `ThemeWindowSkinHost.cs`, `ThemeSkinSurfaces.cs`, `ThemeMaterials.cs`, `ThemeService.cs`, `UserTheme.cs`. **Test:** `DefaultSkinTests.cs`, `ThemeBandWithoutArtTests.cs`, `ThemeFramelessSkinTests.cs`, `ThemeSkinSurfaceRenderTests.cs`, `ThemeSwitchContrastTests.cs`.

- [ ] Add tests for a Hull/Fleet theme with no image dictionary and no image URLs: the band reserves 56 DIP, the child starts below it, frame thickness is 6, title is visible at initial launch, and theme application performs no skin image requests. Existing logo/thumbnail images are not failures.
- [ ] Configure Hull's Fleet appearance through the existing resolved-skin pipeline. Do not overwrite explicit world skin sections or introduce a global hardcoded hull palette for every preset. Preserve current DefaultSkin fallback behavior for other palettes unless an existing explicit owner decision requires common structure.
- [ ] Keep `HostsToolbar=false` for Fleet. Use `BandHeight`/one effective title-height source for drawing, measurement, drag region and native titlebar integration. Never count both native extended-titlebar space and the image-free band's inset twice.
- [ ] Add optional `Accent` color to `WorldThemeSkinEdge` for image-free side rails. Missing means no rail. Draw at fixed thickness 1.5 inside the 6 DIP edge, continuously from the title band's bottom to the bottom frame. The rail does not stop at toolbar/footer/dock boundaries. Clip to valid bounds when the window is very small.

```csharp
// Inside ThemeWindowSkinHost's drawn-frame branch, after the metal face.
// accentBrush is created from the validated Edge.Accent when the theme changes.
var railHeight = Math.Max(0, Bounds.Height - BandHeight - EdgeThickness);
if (accentBrush is not null && EdgeThickness >= 4 && railHeight > 0)
{
    const double railWidth = 1.5;
    const double outerInset = 2;
    context.FillRectangle(accentBrush, new Rect(outerInset, BandHeight, railWidth, railHeight));
    context.FillRectangle(accentBrush,
        new Rect(Bounds.Width - outerInset - railWidth, BandHeight, railWidth, railHeight));
}
```

- [ ] Set Fleet grain/gloss to zero and prevent fallback texture overlays from becoming visible in this path. Use smooth brushes for faces and explicit thin strokes for outlines. Do not change old textured themes.
- [ ] Keep MainWindow's legacy `ThemeBezelHost` and `ThemeOrnamentLayer` inactive for this theme. No stale downloaded overlay may appear on switching from an older image skin.
- [ ] Verify theme switching in both directions, including Hull, Paper, Ember, an explicit bitmap skin and a palette-only world. Check radii, plaque, frame, brush restoration and no accumulating subscriptions.

## Task 4: Integrate the titlebar and docks as working controls

**Modify:** `MainWindow.cs`, `App.axaml`, `Styles/ThemeDockSkin.axaml`, and only where needed `ThemeDockSkinHost.cs` and `Converters/DockChromeConverter.cs`. Inspect `WorkspaceFactory.cs`/`WorkspaceFactory.RightColumn.cs`; retain their docking model and persisted state. Inspect terminal/channel/map views only for their surface bindings.

**Native integration:** Avalonia 12 has `WindowDrawnDecorations` and `WindowDecorationProperties.ElementRole`. Read the installed version's API/templates before editing; do not paste Avalonia 11 titlebar examples. Use the platform's supported drag/caption behavior rather than drawing fake OS buttons. Official reference: https://docs.avaloniaui.net/controls/primitives/windowdrawndecorations

- [ ] Add headless assertions for title bounds and non-overlap with toolbar/native exclusion regions, at launch, toolbar hidden, resized and theme-switched. Preserve real interactions in existing tests, do not merely update screenshot goldens.
- [ ] macOS: retain native traffic lights and their behavior. Windows: retain working minimize/maximize/close, resizing and native caption interaction through supported decorations. Choose one decoration owner per platform so there is not a native titlebar plus an additional fake titlebar.
- [ ] Make unused header background and the plaque/title drag the window. Decorative visuals do not consume input. Use native titlebar roles where supported; if a platform needs `BeginMoveDrag`, keep one guarded handler. Interactive children are not drag targets. Do not have both the native titlebar role and a manual double-click toggle handle the same gesture.
- [ ] Do not assume manual `Maximized` toggling is the correct macOS double-click behavior. Respect the platform path where supported and explicitly report any platform limitation. Native window actions require actual OS testing, not only headless success.
- [ ] Preserve Dock template PART names, `DockableControl`, `ToolChromeControl`, `ToolControl`, drop targets, grips and header button bindings. Draw a single 30 DIP header row with thin surrounding border; never paint an additional header behind/above it. Apply image-free styles independently of `ThemeDockSkinHost.IsSkinActive`.
- [ ] Leave left and right docks independently optional. Test both, left only, right only and neither. Center expands into freed space; no empty reserved rail column. No forced layout reset on skin/session changes. Panel title dragging is docking behavior, not main-window movement.
- [ ] Exercise floating and re-docked tools. They retain readable theme brushes, controls, drag and resize behavior without receiving an accidental second main-window plaque.
- [ ] Terminal transcript, command box, channel transcript/reply and map canvas stay dark. Light panel-body brushes must not overpaint those controls. Check focused, unfocused, selected and disabled text, caret, placeholder and icons; current pale-shell foregrounds must not leak into dark inputs.
- [ ] In narrow docks, prevent map-toolbar wrapping into an accidental second row. Use an overflow menu or compact presentation while preserving all existing commands. Do not rename or add fake mockup buttons that have no app action.

## Task 5: Keep the directory contract small and compatible

Most needed data already exists: `skin.layout.titlebar`, `plaque`, `surfaces`, `radii`, and `edge`. Do not replace the schema or add a second theme engine. The only required additions from this plan are plaque shape `fleet` and optional edge `accent`.

**Client:** `wandur-client/src/Wandur.Core/Discovery/WorldThemeSkin.cs`, `WorldThemeSkinJson.cs`, and Core plaque/skin tests.

**Site:** `wandur-site/src/Wandur.Site.Core/Directory/WorldSkinSanitizer.cs`, `tests/Wandur.Site.Tests/WorldSkinSanitizerTests.cs`, `WorldSkinDirectoryTests.cs`, and `docs/world-themes.md`. Inspect `WorldThemeSanitizer.cs` and `DirectorySnapshotBuilder.cs` for existing flow; change only if fields are lost there.

- [ ] Add failing tests: `fleet` accepted and round-tripped; optional valid `edge.accent` survives client serialization and site sanitization; malformed accent is dropped without losing a valid frame/palette; older shape values remain unchanged.
- [ ] Extend the existing client and site shape allowlists together. Extend the handwritten client reader AND writer for edge accent. Keep skin version 1 and existing validation boundaries. No image URL, generic geometry payload, script or executable XAML is introduced.
- [ ] Add an asset-free fixture to both test suites and test directory sanitation -> client parsing -> rendering. For the skin subsection use this contract, combined with the existing valid top-level palette fixture:

```json
{
  "version": 1,
  "layout": {
    "titlebar": {
      "height": 56,
      "hosts_toolbar": false,
      "title_align": "center",
      "plaque": {
        "shape": "fleet", "cap": 3,
        "fill": "#20292D", "edge": "#424743", "accent": "#8DDEE5",
        "padding": { "left": 60, "top": 0, "right": 60, "bottom": 0 },
        "wings": { "extend": 32, "fill": "#D0D0CA", "edge": "#424743" }
      }
    },
    "panel_header": { "height": 30, "inset": { "left": 8, "top": 0, "right": 8, "bottom": 0 } }
  },
  "surfaces": {
    "titlebar": { "from": "#E7E7E0", "to": "#D0D0CA", "grain": 0, "gloss": 0 },
    "toolbar": { "from": "#D9DAD4", "to": "#D0D0CA", "grain": 0, "gloss": 0 },
    "panel_header": { "from": "#E7E7E0", "to": "#D0D0CA", "grain": 0, "gloss": 0 },
    "panel_body": { "from": "#D8DAD5", "to": "#D8DAD5" },
    "footer": { "from": "#D9DAD4", "to": "#D0D0CA" },
    "ground": { "from": "#424743", "to": "#424743" }
  },
  "radii": { "panel": 2, "control": 3 },
  "edge": { "color": "#D0D0CA", "thickness": 6, "outline": "#424743", "accent": "#8DDEE5" }
}
```

- [ ] Verify JSON field spellings against the current parser when adding the fixture. Preserve existing consumer versions: an older client may omit the unrecognized plaque, but should retain the valid palette/band. Do not bump SDK submodules for desktop-only theme types.
- [ ] Do not rewrite the directory's artwork pipeline, create asset manifests, run image generators, migrate a database or deploy production. Existing JSONB theme storage should not need a database schema migration for these additive fields. Production theme publication is a separate owner action.

## Task 6: Prove it matches and behaves correctly

**Extend:** `wandur-client/tests/Wandur.Desktop.Tests/DefaultSkinCaptureTests.cs`, plus geometry, title, docking, contrast and theme-switch tests above. Update `wandur-client/docs/verification.md` with actual evidence and limitations, not generated mockups.

- [ ] Extend the existing isolated capture harness to exercise actual active-session title changes, not just append a world name into terminal text. Use loopback/synthetic sessions only. Capture WANDUR, LEGENDS OF THE JEDI, a deliberately long title and a Unicode title.
- [ ] Capture at 1040x680, 1380x900 and 1920x1080 DIP, dock combinations and a floating tool. Test display scaling at 100%, 125%, 150% and 200% where the harness/platform supports it. A half-DIP offset is not automatically one physical pixel at every scale.
- [ ] Use an isolated capture directory and current test projects. Example commands from `wandur-client`:

```sh
dotnet build Wandur.sln -c Release
dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Release --no-build
dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Release --no-build
WANDUR_CAPTURE_DIR="/Volumes/Extreme SSD/workspace/wandur/wandur-client/artifacts/fleet-skin-review" dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Release --no-build --filter FullyQualifiedName~DefaultSkinCaptureTests
```

- [ ] Run `dotnet test Wandur.Site.sln -c Release` from `wandur-site` if contract changes are made. Report Docker/database requirements or environmental failures honestly. Do not silently skip affected integration tests and call the contract verified.
- [ ] OPEN the rendered PNGs and compare side-by-side with `concept-macos.png` and `concept-v2.png`. Fix mismatches in geometry/materials before claiming completion. Compilation and property-presence tests do not establish visual fidelity.
- [ ] On an isolated native macOS window, manually verify traffic lights, window drag from plaque and empty header, resize, fullscreen transitions, hidden toolbar, docking and text entry. On Windows verify caption controls, maximize/restore, drag/resize and snap behavior. If a platform is unavailable, label it unverified, not supported-and-tested.

### Acceptance checklist

- [ ] One coherent pale metal header, not a blank OS bar above a second decorative one.
- [ ] Shallow fixed-width silver shoulders around a rectangular dark plaque, not nested pointed hexagons.
- [ ] Full LEGENDS OF THE JEDI fits at 1380 DIP; truly centered regardless of dock visibility and asymmetric caption controls.
- [ ] Longer names expand the middle until its limit, then ellipsize; no stretched lamps, smaller font, wrapping or changed band height.
- [ ] Plaque and all of its paint remain inside the title band; nothing overlaps the toolbar or OS controls.
- [ ] Continuous thin cyan outer side rails, clean simple corners, no scratches/noise/individual armor plates.
- [ ] Single slim functional header per dock; no decorative frame consuming large content areas.
- [ ] Dark MUD content and inputs with readable normal, placeholder, disabled and selected states.
- [ ] Dock layouts remain user-controlled and survive theme/session changes.
- [ ] Fleet skin works offline without image assets. Logo and world artwork are independent.
- [ ] Real platform behavior has been tested or explicitly reported unverified.

### Delivery to the owner

Provide a short list of changed files and their responsibilities, actual screenshot paths, commands/results, and remaining platform caveats. Separate completed work from unverified behavior. Do not say "matches the mockup" without opening and reviewing the actual rendered captures. Do not push, deploy, or include unrelated working-tree edits in a commit.

## Scope of this handoff

Prepared using the writing-plans skill after inspecting the current local code and design references. Only this documentation was created in this turn. No application changes, native UI verification or implementation test runs have been performed by the author of this handoff.
