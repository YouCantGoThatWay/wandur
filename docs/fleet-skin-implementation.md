# Fleet skin implementation and visual review

## Safety and scope

Implementation was developed in the separate `wandur-client-fleet` worktree on
`codex/fleet-skin-reference`, then integrated into local `main`. The follow-up
native crash fix and universal geometry correction use `fix/fleet-theme-switch`
in that same separate worktree.
Commit `3e7c520`, also named by `backup/fleet-skin-start`, checkpoints the existing
source changes before this implementation. Retain that checkpoint for comparison
and rollback. Local integration includes the preceding world-skin support on
which Fleet depends. No remote push or deployment is included.

This change covers the client renderer and its JSON reader. It does not change
the `wandur-site` schema editor or publish a world theme.

## Selecting and understanding the skin

Fleet is the base appearance for every theme, not a mode selected with Hull.
Hull supplies the reference silver-gray palette and dark work areas. Other
presets, personal palettes and enabled world themes retain their colors while
using the same Fleet chassis. Existing palette preferences are not migrated.

All new skin decoration is drawn in Avalonia, without bitmap assets. The frame
uses continuous cyan side accents, restrained pale-gray gradients and fixed-size
details. A measured title plaque remains centered in the window, expands for
longer titles and ellipsizes inside its lamp-safe area. Native caption controls
remain native. The header uses Avalonia's native title-bar role.

The title band is 50 DIP high, the plaque 60 DIP high, and its usual width is
608-760 DIP, limited by symmetric platform caption exclusions. Title text is
20 DIP with 64-DIP horizontal clearance. Dock headers are 38 DIP high.
The plaque projects 12 DIP into the toolbar, which reserves an upper ledge so
its buttons remain clear. Hiding the toolbar preserves clearance for that lip.
Raised silhouette edges, a short lower extrusion and a recessed dark plate
provide depth without textures. The surrounding title cap and toolbar have
matching bevels and a cooler silver-gray finish.

The toolbar now paints a shaped receiving socket in its own vector background,
not just a rectangle behind the plaque. A continuous dark channel follows the
titlebar seam, descends around both shoulders, and runs beneath the plaque's
lower lip. A reflected highlight follows the receiving edge. The socket tracks
the title's measured width and actual arranged position. Its geometry is cached
and refreshed when the palette or window arrangement changes.

Fleet toolbar buttons use original vector icons and localized labels, with
labels hidden below 1100 DIP. The new Settings button opens the existing
preferences dialog. Connect retains its existing open-session behavior; it is
not mislabeled Reconnect. The search action retains the localized Find a MUD
label. Dock grips sit to the left of their titles; pins, close marks and menu
chevrons have matching vector faces while retaining their real Dock commands.
Windows keeps its fallback menu below the title/toolbar pair.

Real dock controls remain in use. Left and right panels can be hidden
independently, freeing the available space for the terminal. Existing terminal
font preferences, session functionality and docking proportions are preserved;
the reference illustration's exact content density is not imposed on users.

## Implementation locations

- `src/Wandur.Desktop/FleetSkin.cs`: palette, gradients and built-in skin defaults.
- `src/Wandur.Desktop/FleetIcons.cs`: consistent vector toolbar symbols and live localized captions.
- `src/Wandur.Desktop/FleetToolbarSurface.cs`: shaped toolbar receiving socket, seam shadow and reflected edge.
- `src/Wandur.Desktop/FleetTitleLayout.cs`: measured title sizing and caption clearance.
- `src/Wandur.Desktop/ThemePlaque.cs`: image-free plaque geometry and lamps.
- `src/Wandur.Desktop/ThemeWindowSkinHost.cs`: frame, continuous rails and header joints.
- `src/Wandur.Desktop/MainWindow.cs`: live title, native title-bar role and toolbar layout.
- `src/Wandur.Desktop/ThemeDockSkinHost.cs` and `Styles/ThemeDockSkin.axaml`: actual Dock chrome.
- `src/Wandur.Desktop/Styles/Fleet.axaml`: map toolbar, channel tabs and zoom control states.
- `src/Wandur.Desktop/ThemeService.cs`: skin activation and reusable brush resources.
- `src/Wandur.Desktop/Views/SessionContentView.cs`: terminal document frame.
- `src/Wandur.Desktop/Views/TerminalView.cs`: readable composer icons and theme reattachment.
- `src/Wandur.Core/Discovery/WorldThemeSkin*.cs`: optional edge accent and reusable `fleet` plaque shape.

Every theme uses the same 50-DIP title band, 60-DIP projecting plaque, 38-DIP dock
headers, 6-DIP frame and 2/3-DIP panel/control radii. Legacy frame bitmaps,
ornaments, shape names, dimensions and radii remain readable metadata but do not
replace the chassis. This applies even when their images have already loaded.

## Customization boundary

World themes already expose nine palette colors (shell, panel, terminal, text,
muted, accent, secondary accent, border and terminal text). Skin metadata adds
per-surface gradients, gloss, bevel strength, grain and accent rules, plus plaque,
wing and frame colors. Personal themes have additional control and ANSI colors.

Palette changes now recolor Fleet without disabling its geometry, toolbar
socket, vector icons or docking frames. `DefaultSkin.Merge` accepts world paint
per surface and plaque/wing/frame colors, while ignoring replacement geometry.
`FleetSkin.SynchronizeMaterials` publishes the current materials and interaction
state colors. Hull retains its reference gradient stops; fine lighting details
still contain fixed shading colors rather than exposing every pixel as a token.

The existing `images.chrome` material supports low-opacity tiled PNG textures
on shared chrome, including the titlebar and plaque wings, within the fixed
geometry. Explicit `skin.surfaces` values override those materials per slot.
It is not a dedicated plaque-face texture slot. That narrower override still
needs matching client and site schema/editor support; no site changes or theme
publication are part of this correction. The legacy image cache remains
compatible, although replacement frame and ornament images are not rendered.

## Original reference-render verification on 2026-09-23

`dotnet test Wandur.sln -c Release -m:1` completed successfully:

- Core: 725 passed, zero failed.
- Desktop: 480 passed, zero failed.

Regression tests cover title placement and long-title clearance, narrow windows,
independent dock hiding, theme changes on cached views, readable controls,
compact map toolbars, consistent dock gaps, world-defined short title bands and
legacy bitmap title-bar height.

Updated headless Avalonia captures are under `artifacts/fleet-recess-review/`:
`fleet-1536x1024.png`, `fleet-1380x900.png`, `fleet-1040x680.png`,
`fleet-long-title.png`, and matching `fleet-no-docks-*.png` files. These are real
control renders with synthetic loopback-session data, not generated mockups.
`fleet-header-detail.png` renders the same live visual into a short viewport for
inspecting the join. Pixel-level regressions check visible dark recesses below
the plaque and beside its shoulders, while keeping the usable toolbar face light.

The depth-refinement regressions verify toolbar overlap, button clearance,
label collapse, hidden-toolbar clearance, live theme reparenting and map hover
feedback. Independent review identified fallback-menu ordering, stale toolbar
spacing and overridden interaction states; these are addressed. The deeper
metal initially failed the 4.5:1 muted-text contrast check at 4.42:1. Darkening
the muted foreground corrected it. The final full run passes contrast checks.

An intermediate full run also had an Avalonia cross-thread cleanup error in
`MappedVitalsLiveTests.TheOpponentCardShowsInTheSecondFight`. It did not recur
in the final full run; no change was made to that unrelated test.

Native macOS preview confirmed title-plaque double-click maximize/restore.
Traffic-light vertical alignment is still outstanding. Native dragging has not
been conclusively verified by a position assertion. Windows caption behavior
and rendering require a native Windows pass; headless captures do not prove it.
Attempts to inspect the refreshed native preview in this refinement timed out
in the computer-use service, so no new native interaction result is claimed.

The isolated native preview reported a database-open warning and used defaults.
Normal user data was not opened or changed. Populated terminal visual checks
therefore rely on the tested loopback capture fixture, not that native preview.

`python3 scripts/generate-localization.py --check` reports `Strings.cs` out of
date in both this worktree and the unchanged original checkout. That existing
generated-file drift was not regenerated as part of the skin change.

The implementation is ready for further visual review, not a claim of exact
cross-platform pixel equivalence to the reference illustration.

## Native theme-switch crash correction

A subsequent macOS report exposed a stack overflow when activating Hull. The
Fleet title layout requested a native titlebar height of 50 DIP while the
general macOS path immediately requested 52 DIP. Each native margin change
synchronously re-entered the layout method, alternating the two values until
the process aborted. Headless rendering did not simulate those native callbacks.

The title layout now resolves and writes the native height in one place. The
Preferences regression first failed with a 50/52 mismatch and now verifies that
switching to Hull and resizing never request a height different from its band.
An isolated native run reproduced the original stack overflow; after the fix,
startup and eight live Paper/Hull switches through the Preferences view model,
with resizing after each switch, completed and shut down with exit code 0.
The probe used temporary data and an offline directory URL, not user profiles.

Native decoration-margin and resize notifications are now coalesced onto a
later dispatcher pass, never a recursive call back into the current title
layout stack. The resolved native height stays 50 DIP across palette switches.

## Universal geometry regression coverage

`FleetPaletteTests` exercises every preset against the same plaque bounds,
recessed toolbar and docking chrome, plus personal colors, world paint,
legacy artwork rejection and a chrome texture applied to the fixed shape.
Legacy frame tests now assert shared Fleet geometry while retaining docking,
close/collapse and asset-cache checks. Existing Hull pixel-level recess tests
still protect the reference header and toolbar join.

Verification: Core 725 passed; Desktop 496 passed. The native macOS probe
switched through all 11 presets twice via Preferences and resized after each
switch, retaining a 50-DIP titlebar throughout and exiting successfully.
Review renders cover every preset; the Hull reference pixel checks still pass.
An additional regression isolates plaque color from terminal instrument bars.

One intermediate Desktop run exposed a map-reconnect test race: connection
completion does not imply background map restoration has completed. The test
now awaits the existing `MapReady` task before asserting restored rooms; no
production mapping code changed. The updated test and all palette tests passed
together in a 16-test focused run. Native Windows remains unverified.

## Compact chrome and directory refinements (2026-09-23)

These refinements remain in `wandur-client-fleet`, branch
`design/fleet-material-polish`, and are not yet merged into the original
`wandur-client` checkout's `main`.

- Side docks physically touching the main workspace's left or right edge omit
  that outside rim and margin. Interior edges and floating docks retain their
  frames. The decision follows arranged bounds, not a dock's alignment label.
- Footer shortcut hints are vertically centered. Top toolbar labels and the
  world selector use 13-DIP text; dock/document headings retain 14-DIP text.
- Map and Channels toolbars share Workspace's metal and ink. Map actions use
  26-DIP targets. Search, Follow, Fit and More stay in the primary row; Stop
  remains available during walking. Floor navigation and the single labeled
  grid checkbox live in More, or in the editor's permanent inspector. Expanded
  search occupies a separate row and is tested at 240 DIP.
- Channel tabs have tighter spacing and padding without changing message or
  reply colors. Terminal, map and channel work areas retain their palette.
- Find a MUD has a full-width search field, a clearer page heading, and compact
  result cards with identity tiles. Existing selected-world artwork, filtering,
  scroll restoration and connection actions remain in place. Result cards do
  not introduce extra artwork requests.
- On macOS the title band's perimeter is flat metal instead of an inset bevel
  close to the traffic lights. Native button positions are unchanged. The
  central plaque, receiving socket and fixed 50-DIP native titlebar height are
  preserved. Windows keeps the original cap bevel.

Actual Avalonia renders are in `artifacts/refinement-round2/`, including
`fleet-1380x900.png` and `directory-polish-{hull,slate}-{1040x680,1536x1024}.png`.
These are control renders, not generated design images. The native Mac probe
completed 22 preset switches and resizes with temporary data. The inspection
tool could not resolve that standalone native process for a screenshot, so
native traffic-light spacing and Windows appearance still need visual review.

Verification: 529 Desktop tests and 725 Core tests passed. The first full
Desktop run had one timeout in the untouched script-login packet fixture;
it passed in the focused rerun and the final full run. Updated layout tests
now reflect 13-DIP toolbar text and floor controls behind More. Independent
review found no remaining product issues. The pre-existing localization
facade drift noted above remains unchanged; no localization files were edited.
