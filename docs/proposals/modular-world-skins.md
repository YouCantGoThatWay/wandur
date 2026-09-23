# Modular world skins: optional docks and corner flares

Status: implementation proposal for Cursor, September 22, 2026. No application behavior has been changed by this proposal.

## Requested result

Match the supplied weathered silver and cyan reference closely, remove all ORION SYSTEMS branding, and preserve the option of flared lower corners. The app must work with both side docks, left only, right only, or neither. Map and Channels can be stacked, individually hidden, pinned, moved or floated. The central transcript, composer, toolbar and status bar remain live controls.

Use separate artwork for the window frame and dock panels. The theme describes decoration, never the docking arrangement or whether a tool is open. Treat the supplied screenshots as visual references only. Do not reproduce their game transcript, credentials or personal session state in fixtures.

## Existing implementation, verified in this checkout

| Responsibility | Existing source |
| --- | --- |
| Theme v1 models, handwritten read/write converter | `src/Wandur.Core/Discovery/WorldTheme.cs` |
| PNG URL resolution, disk cache, size checks | `src/Wandur.Core/Discovery/WorldCatalog.ThemeImages.cs` |
| Async decode, cancellation, active-theme check and disposal | `src/Wandur.Desktop/SessionWorkspace.ThemeImages.cs` |
| Active-session appearance selection | `src/Wandur.Desktop/SessionWorkspace.Appearance.cs` |
| Batched palette resources and theme events | `src/Wandur.Desktop/ThemeService.cs` |
| Existing eight-patch window border, omitting center | `src/Wandur.Desktop/ThemeBezelHost.cs` |
| Shell, native titlebar, toolbar, footer and docking host | `src/Wandur.Desktop/MainWindow.cs` |
| Left Workspace, central documents, stacked right tools | `src/Wandur.Desktop/WorkspaceFactory.cs` |
| Removal/restoration of an empty right column | `src/Wandur.Desktop/WorkspaceFactory.RightColumn.cs` |
| Live Dock header/body styling | `src/Wandur.Desktop/App.axaml`, `Converters/DockChromeConverter.cs` |
| Script accordion, separate from shell docks | `src/Wandur.Desktop/Views/ScriptPanelRailView.cs` |

The site stores themes as JSONB in `WorldTheme.Theme`, imports through `WorldThemeSanitizer`, and attaches JSON to directory records by hostname. Theme types currently belong to client Core, not the SDK. Keep this change within client/site; an SDK extraction is not required.

Important current limitations:

- `ThemeBezelHost` uses the source slice widths as destination layout-unit widths. A high-resolution asset can therefore produce oversized borders.
- `Bitmap.DecodeToWidth(..., 2048)` can change source coordinates while the slice metadata remains unchanged.
- Existing rendering intentionally omits the center patch. A full-window PNG cannot supply the terminal background through that path.
- A central plaque baked into the horizontal middle patch stretches with the window. Extract it as a fixed-size overlay.
- `ReadFrame` is permissive about some wrong-typed optional fields; the site accepts numeric strings and can accept a bezel without a border, while the client requires a border. Site normalization validates a constructed object but returns original raw JSON on success.
- Neither `plaque` nor `accent` is actually drawn by `ThemeBezelHost`. Removing generated text is an asset task, not just deleting `plaque` from JSON.
- Active-session theme selection and right-column collapse already exist. Preserve them.

## Compatibility decision

Keep directory `schema_version: 2` and theme `version: 1`. Add one optional top-level `skin` object with its own `version: 1`. Keep legacy `frame.kind: bezel` unchanged as a fallback. Do not reinterpret the units of legacy fields.

A new client prefers a valid, successfully loaded `skin.window`; otherwise it renders the legacy frame, then palette-only if that cannot load. Panel skin loading is independent. An old client ignores `skin` and continues to render the legacy bezel and palette.

Unknown skin versions are ignored. A malformed skin must never invalidate the colors or world listing. A malformed optional overlay is dropped individually; a malformed panel style affects only that style; a malformed window definition drops that window definition. No `IsValid` dependency from the required theme palette to optional decoration.

Do not put panel visibility, mandatory widths, tools, connection commands, XAML, scripts, fonts or layout trees in the skin. Existing layout and user actions remain authoritative.

## Proposed wire contract

The adjacent `modular-world-skin.example.json` is syntactically valid sample data. Its new industrial URLs and dimensions describe assets to prepare, not files already hosted or measured exports. The legacy fallback URL exists in the current site.

```json
{
  "version": 1,
  "id": "wandur-industrial-v2",
  "name": "Industrial silver",
  "variant": "light",
  "corner_radius": 2,
  "surface": "metallic",
  "colors": {
    "shell": "#E8ECF0",
    "panel": "#F2F5F8",
    "terminal": "#FFFFFF",
    "text": "#1A2330",
    "muted": "#5A6A7A",
    "accent": "#2AA8D8",
    "accent_secondary": "#28799B",
    "border": "#B8C4D0",
    "terminal_text": "#15202B"
  },
  "frame": {
    "kind": "bezel",
    "inset": {
      "left": 28,
      "top": 36,
      "right": 28,
      "bottom": 32
    },
    "content_radius": 0,
    "assets": {
      "border": {
        "url": "themes/imperial-bezel/border.png",
        "slice": {
          "left": 48,
          "top": 56,
          "right": 48,
          "bottom": 48
        }
      }
    }
  },
  "skin": {
    "version": 1,
    "window": {
      "border": {
        "url": "themes/industrial-v2/window-border.png",
        "source_size": {
          "width": 512,
          "height": 384
        },
        "slice": {
          "left": 48,
          "top": 128,
          "right": 48,
          "bottom": 80
        },
        "thickness": {
          "left": 24,
          "top": 64,
          "right": 24,
          "bottom": 40
        }
      },
      "inset": {
        "left": 24,
        "top": 64,
        "right": 24,
        "bottom": 40
      },
      "overlays": [
        {
          "id": "header",
          "url": "themes/industrial-v2/header.png",
          "anchor": "top-center",
          "size": {
            "width": 320,
            "height": 64
          }
        },
        {
          "id": "left-flare",
          "url": "themes/industrial-v2/corner-left.png",
          "anchor": "bottom-left",
          "size": {
            "width": 120,
            "height": 64
          }
        },
        {
          "id": "right-flare",
          "url": "themes/industrial-v2/corner-right.png",
          "anchor": "bottom-right",
          "size": {
            "width": 120,
            "height": 64
          }
        }
      ],
      "footer_clearance": {
        "left": 100,
        "right": 100,
        "min_height": 24
      },
      "compact_below": {
        "width": 1200,
        "height": 760
      }
    },
    "panels": {
      "default": {
        "border": {
          "url": "themes/industrial-v2/dock-panel.png",
          "source_size": {
            "width": 512,
            "height": 1024
          },
          "slice": {
            "left": 32,
            "top": 80,
            "right": 32,
            "bottom": 32
          },
          "thickness": {
            "left": 16,
            "top": 40,
            "right": 16,
            "bottom": 16
          }
        },
        "inset": {
          "left": 16,
          "top": 8,
          "right": 16,
          "bottom": 16
        },
        "header_height": 32
      }
    }
  }
}
```

### Units and validation

New skin fields use strict JSON numbers, never numeric strings; require finite values. The old frame parser/validator behavior is outside this additive change.

| Field | Contract |
| --- | --- |
| `skin.version` | Integer 1; other versions ignored |
| `window.border`, `panels.default.border` | Required within their respective definitions; PNG nine-slice descriptor |
| `url` | Reuse existing URL policy: HTTPS absolute without userinfo, or safe relative path resolved by `WorldCatalog.BaseUri`; same length and path guards |
| `source_size` | Required integer raw PNG pixel dimensions; each 1..4096, total at most 8,388,608 |
| `slice` | Required four nonnegative integer source-pixel margins, each at most 4096; horizontal and vertical sums strictly below declared source dimensions |
| `thickness` | Required four destination margins in DIPs, each 0..128; independent of source resolution |
| `inset` | Required four layout margins in DIPs, each 0..128 |
| `window.inset` | Each side at least matching border thickness; reserves the ordinary rectangular frame |
| `panels.default.inset` | Padding around the combined live header/body; left/right/bottom at least corresponding border thickness |
| `header_height` | Required DIP row height 24..48; inset.top + header_height at least border.thickness.top |
| `overlays` | Optional array, maximum 3 entries; one per allowed anchor, unique nonempty ASCII identifier of at most 40 characters |
| `anchor` | Only `top-center`, `bottom-left`, `bottom-right` for this version; positions relative to window frame host, not screen |
| `size` | Overlay destination DIP size, positive, width at most 512, height at most 128; source image aspect ratio must match within 1% |
| `footer_clearance` | Optional left/right DIP padding 0..160 and minimum height 0..48; required if a bottom overlay extends above window.inset.bottom |
| `compact_below` | Optional dimensions in DIPs, width 640..2560, height 480..1600; if either host dimension is smaller, hide all overlays and remove their footer clearance |
| Unknown keys | Ignore for behavior; site preserves them for forward compatibility |

Defaults: omitted overlays = none; omitted footer clearance = zero; omitted compact threshold = no automatic compact behavior. An omitted panel style means existing Dock appearance. The only panel style key in v1 is `default`; future role overrides can be additive.

At decode time verify actual PNG dimensions equal `source_size` before using source rectangles. Reject that component on mismatch. Do not clamp a bad slice into a guessed valid image. PNG limits remain 4 MiB, 4096 per dimension, 8,388,608 pixels. For overlays, check the same file/pixel limits and the declared destination aspect ratio after decoding.

Limit each skin to 5 distinct image URLs, 16 MiB total received PNG data, and 64 MiB total decoded RGBA pixels. Deduplicate identical resolved URLs; panels never get separate bitmap copies. Load legacy fallback only if needed. Existing shell/chrome texture budgets stay intact.

### Failure boundaries and sanitizing

Use shared JSON fixture cases copied into the two repos to keep rules identical without coupling site to the desktop/Core assembly.

1. Nonobject skin, invalid version or more than the bounded schema structure supports: omit skin.
2. Bad window metrics or border descriptor: omit window; retain valid panel skin and legacy frame.
3. Bad overlay: omit that entry; retain the base window border and other entries.
4. Bad panel definition: omit default panel skin; retain window.
5. Footer geometry cannot safely contain a bottom overlay: omit the offending overlay, not the palette.
6. Remove empty understood skin sections. Preserve unknown keys on otherwise valid sections; never execute or download resources referenced only by unknown keys.
7. Runtime missing/corrupt image: use the fallback for the affected component and release only its layout reservation.
8. Keep source theme data immutable during rendering. Runtime readiness is separate from schema validity.

The site must sanitize skin even when no legacy `frame` property exists. Refactor the current early-return structure. Continue using legacy frame validation independently; do not silently tighten all historic themes while adding skin.

## How the UI renders it

```text
Theme window host
  base window border drawn behind shell
  live shell: toolbar, notice, existing DockControl, footer
    Workspace tool: optional generic dock skin + live header/body
    Session: live transcript/composer, no baked-in labels
    Map tool: optional generic dock skin + live header/body
    Channels tool: optional generic dock skin + live header/body
  fixed-size header / lower corner overlays drawn above decoration
```

Do not scale the whole application through a fixed-size Viewbox. Let existing Avalonia layout size the live controls; only the images resize. Avalonia's ordinary `Image.Stretch=Fill` distorts aspect ratio, so the frame requires patch drawing and the detached ornaments require uniform scaling. See [Image scaling](https://docs.avaloniaui.net/controls/media/image).

### Frame and nine-slice drawing

Extract reusable pure patch geometry from `ThemeBezelHost.Render`. Inputs are source pixel size, source slice, destination DIP thickness, and destination bounds. Output is eight source/destination rectangle pairs. Never draw the center for border assets.

The four corners keep authored proportions. The sample uses a uniform 2 source pixels per DIP for its corner patches. Horizontal edge strips stretch only horizontally; vertical strips stretch only vertically. Put distinctive large lamps, plaques and flares outside stretch regions. Prepare an actual repeat-safe edge before adding an optional tiled mode in a future version.

If a component becomes smaller than the sum of its border thicknesses or cannot fit its live controls, use plain palette chrome and release its skin inset. Do not compress the terminal or distort corner patches to satisfy decorative minima. Snap shared destination boundaries together to prevent seams at fractional display scales.

Keep full-resolution decoded border images under the bounded loader limits for this version. If downsampling is retained, carry the actual source-to-decoded ratio and transform every source rectangle; do not use unscaled slice metadata. Explicitly handle image DPI metadata using the pinned Avalonia image API, and test 96/192 DPI PNGs at the same raw pixel dimensions.

### Corner flares and useful area

The lower flares are stationary ornaments attached to the main frame, not to a left or right dock. They remain when both docks are hidden. Each uses a transparent PNG with no printed labels. The sample draws each at 120 x 64 DIPs and the base bottom inset is 40 DIPs.

This means only 24 DIPs of the flare project into the live shell, into the footer row. Set that row's minimum height to 24 and add 100 DIPs of local clearance at each occupied end. With a 24-DIP left frame inset, the left flare overlaps the shell horizontally by 96 DIPs; the 100-DIP footer clearance includes a 4-DIP gap. Mirror this math at the right. The body and composer end above the footer, so they remain rectangular and unobscured.

Reserve left clearance only while the left flare is loaded/visible, right clearance only for the right flare, and minimum footer height while either requires it. Add clearance to existing footer padding, not to the whole DockControl. Ellipsize status text and hide/truncate the existing nonessential shortcut hint before sacrificing terminal area.

For a bottom overlay with height H and base bottom inset B, require H <= B + footer_clearance.min_height. For width W and side inset S, require the corresponding footer clearance >= max(0, W - S) + 4 whenever it intrudes above the bottom inset. A top-center overlay must fit entirely within the top inset.

If geometry no longer fits, hide ornaments first. The sample compact threshold suppresses ornaments below 1200 x 760. Determine the threshold from host bounds once, not from a content size already reduced by decoration, to avoid resize oscillation. Keep the base border and ordinary window inset; remove only ornament-specific clearance.

This provides most of the reference's corner character with a local cost at the ends of the status bar. A flare tall enough to cover transcript rows would need more reserved space or an irregular content layout; that is deliberately not the default. Clipping a live control does not make obscured content usable.

### Layering, input and native window behavior

Create an overlay sibling above the shell; drawing ornaments only in a Decorator's background render can let child controls cover them. The overlay is nonfocusable and `IsHitTestVisible=false`. Never set that property on the host containing live controls. Decoration should not intercept the map, composer, splitter, close button or dock grip. See [Avalonia hit testing](https://docs.avaloniaui.net/docs/graphics-animation/hit-testing).

Keep all ornament drawing within the window client bounds. Do not require transparent OS windows or replacement native controls. The alpha exterior is composited over the normal window background. Preserve existing macOS traffic-light space, titlebar drag/double-click, maximize/fullscreen handling and fallback menus. The screenshot's host-window title text is not app content to reproduce.

Start with the existing shell wrapping strategy and measure its header cost. Do not add an additional duplicate toolbar. If the new host moves the toolbar relative to the native titlebar, update the safe-area/drag tests and keep controls clear of native buttons. Use a smaller prepared top bezel if necessary; do not solve this by disabling native chrome.

### Optional docks and stacking

Apply the generic skin once around each actual visible ToolDock's combined header/body, using the pinned Dock 12.1.0.6 control theme. Inspect the package template before modifying it. Styling only `WorkspaceTool.Build()` would miss the live Dock header.

The skin host reserves inset.top before a live header row of header_height, then lays out the existing tool content. The sample reserves 8 DIPs above a 32-DIP header, matching a 40-DIP painted top border. Header buttons, localized title, grip, tabs, menus and automation peers remain native controls. Suppress the existing header/body backgrounds and borders only when that skin image is ready, scoped to the new host, so they do not cover the artwork or leave blank surfaces after a failure.

Use one skin per ToolDock, not per tab, and two instances for vertically stacked Map and Channels. Floating and auto-hide popup tools get the same generic skin inside their existing host chrome, without window flares. In compact or too-small tool bounds, use the existing plain Dock template.

Do not recreate `WorkspaceFactory`, reset panel proportions or restore closed tools on a theme change. Preserve the empty-right-column fix and verify the left column also disappears. A hidden dock has no decoration, minimum width, margin or orphan splitter left behind.

Script rails are a distinct session-local system. This phase preserves their current styling/behavior and does not move them into shell docks. Extend the same visual primitive there only as a separate follow-up.

## Asset handoff

The current generated templates are saved outside the repos at workspace `assets/wandur-ui-template-v2/`:

- `window-shell.png`: 1536 x 1024, unbranded full-width window concept.
- `dock-panel.png`: 887 x 1774, unbranded generic panel concept.
- Original `assets/orion-ui-template/orion-shell.png` remains a material/corner reference, not a release asset because it contains ORION SYSTEMS text.

These are design/source templates, not a finished production slice set. Having an alpha channel alone does not prove the whole exterior is cleanly transparent. Inspect masks and remove the rendered backdrop during asset preparation. Do not feed the full window concept directly to the old border loader or make it a tiled chrome image.

Prepare and measure five production exports: `window-border.png` (center unused, no plaque), `header.png` (blank, transparent), `corner-left.png`, `corner-right.png` (transparent, pronounced asymmetrical corner shapes), and `dock-panel.png` (border/title material only, blank center or center ignored). Source dimensions in the example are target export dimensions, not claims about today's files. Update the fixture to the actual measured exports.

Remove all stencil branding and labels, not just the central plaque. Live titles come from the application. Review at 1x and 2x, at least two window aspect ratios, both-side/one-side/no-side layouts, and a short stacked right panel. Do not silently stretch the existing tall panel texture into a short wide map.

Host finished files in site `src/Wandur.Site/wwwroot/themes/industrial-v2/`, not a repo-root `wwwroot/`. Version the path when bytes change because the client cache key is the URL. Do not overwrite the current `imperial-bezel` fallback.

## Site work and release boundaries

Extend `src/Wandur.Site.Core/Directory/WorldThemeSanitizer.cs`; update `docs/world-themes.md` and theme tests. The current JSONB theme column and `DirectorySnapshotBuilder` JSON pass-through need no database migration or directory DTO schema bump. Import still flows through `SnapshotImporter.UpsertThemeAsync`.

Add component-isolation validation tests and verify a sanitized theme survives import, JSONB storage and /directory output with the same known skin fields. Account for the directory cache's normal five-minute refresh; do not claim a separate import process instantly invalidates a running server's cache.

Keep a palette-only fixture and the old bezel fixture. Add an isolated industrial fixture rather than rewriting the entire captured live directory snapshot for one theme. Existing `ThemeAssetTests` imposes a 200 KB ceiling on the old tiny bezel; do not apply that arbitrary old test limit to new detailed PNGs. Apply the loader's actual limits to new assets.

Publishing/production seeding is separate from local implementation. No SDK/submodule edits, production DB changes, pushes or Azure deployment are required to implement this proposal.

## Acceptance

1. No ORION SYSTEMS or other generated branding appears.
2. New/legacy/palette-only themes all render, with no stacked duplicate frame.
3. Four dock combinations work; right Map/Channels stack independently; hiding the last side tool reclaims the column and splitter.
4. Flares remain fixed in size and anchored when docks toggle; no footer, terminal, composer or native window control is covered.
5. Artwork never receives keyboard focus or blocks mouse input.
6. Failed assets remove their own chrome/clearance and preserve valid siblings and palette.
7. Late downloads from a previous session never repaint the active session; theme switches do not restart connections or reset dock positions.
8. Client/site fixture validation agrees, including all new numeric units, version handling and invalid-component cases.
9. Source pixels and destination DIPs stay distinct at 100%, 150% and 200% display scaling.
10. Existing theme contrast and docking tests pass; visual captures verify textures, seams and corner proportions.

Implementation checklist: [Cursor implementation plan](../superpowers/plans/2026-09-22-modular-world-skins.md).
Site entry point: sibling repository `wandur-site/docs/proposals/modular-world-skins.md`.

