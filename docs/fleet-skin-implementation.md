# Fleet skin implementation and visual review

## Safety and scope

Implementation was developed in the separate `wandur-client-fleet` worktree on
`codex/fleet-skin-reference`. The original `wandur-client` checkout is unchanged.
Commit `3e7c520`, also named by `backup/fleet-skin-start`, checkpoints the existing
source changes before this implementation. Retain that checkpoint for comparison
and rollback. Local integration includes the preceding world-skin support on
which Fleet depends. No remote push or deployment is included.

This change covers the client renderer and its JSON reader. It does not change
the `wandur-site` schema editor or publish a world theme.

## Selecting and understanding the skin

Select the built-in Hull theme. World themes take precedence when enabled; turn
off world themes to review Hull independently. Personal themes are not replaced.
Hull is the default for new settings, not a forced migration of existing choices.

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
and normal theme backgrounds are restored when leaving Fleet.

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

World-defined Fleet plaques retain their own band height, colors and metadata;
using the shape does not enable the whole built-in Hull layout.

## Customization boundary

World themes already expose nine palette colors (shell, panel, terminal, text,
muted, accent, secondary accent, border and terminal text). Skin metadata adds
per-surface gradients, gloss, bevel strength, grain and accent rules, plus plaque,
wing and frame colors. Personal themes have additional control and ANSI colors.

The built-in Fleet renderer still contains fixed gradient stops and detail
colors. Applying a world or personal theme leaves the built-in Fleet mode;
these controls are not yet an override layer that preserves its entire shape,
toolbar socket and styling.

The existing `images.chrome` material supports low-opacity tiled PNG textures
on shared chrome. It is not a dedicated Fleet-titlebar texture slot. A future
extension can select Fleet as a base skin, expose its material colors, and clip
an optional directory-hosted texture to the existing titlebar/plaque geometry,
then paint its bevels, shadows, lamps and text above that texture. This preserves
the shape and stretch behavior without requiring a replacement frame image.
Such an extension needs matching client parsing/rendering and site schema/editor
support; it has not been implemented or published by this change.

## Verification on 2026-09-23

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
