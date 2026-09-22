# Modular World Skins Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> Cursor without those skills can follow the same tasks directly. This is a handoff plan, not a record of implemented changes.

**Goal:** Render the unbranded industrial skin with independently optional side docks and fixed-size lower corner flares.

**Architecture:** Add optional theme.skin v1 while retaining theme/frame compatibility. Reuse the client theme-loading lifecycle and Dock layout, separating scalable border patches, fixed-size ornaments and native controls. Site remains the JSON validator/distributor and static asset host.

**Tech Stack:** Existing .NET 10, Avalonia 12.1.2, Dock 12.1.0.6, xUnit/Avalonia.Headless, ASP.NET Core, EF Core and Postgres. No new runtime dependency is expected.

**Spec:** `docs/proposals/modular-world-skins.md`; sample `docs/proposals/modular-world-skin.example.json`. Site-specific instructions are in sibling repo `wandur-site/docs/proposals/modular-world-skins.md`.

## Global constraints

- Theme version remains 1; directory schema remains 2; skin has its own version 1.
- The user asked for a proposal. Execute only when this plan is supplied as an implementation request.
- Keep native text/input/docking. No fixed application canvas or rasterized UI labels.
- No forced panels, layout reset, native control replacement, session restarts or copied game transcripts.
- New image files are bounded PNGs and shared per theme, never decoded per panel.
- Preserve unrelated changes; current inspection found a modified SDK submodule and untracked repo-root wwwroot in site. Recheck rather than overwriting either.
- No SDK work, migrations, production seeding, pushes or deployment are part of this change.
- Existing repo handoff, identity, localization and verification rules apply. Do not commit automatically from this proposal.

## Task 1: Define and round-trip the optional skin contract

**Files**
- Modify `src/Wandur.Core/Discovery/WorldTheme.cs`.
- Create `src/Wandur.Core/Discovery/WorldThemeSkin.cs`.
- Create `src/Wandur.Core/Discovery/WorldThemeSkinJson.cs`.
- Create `tests/Fixtures/world-theme-industrial-skin.json` using the proposal's example after production asset dimensions are settled.
- Create `tests/Wandur.Core.Tests/WorldThemeSkinTests.cs`.
- Preserve `WorldThemeFrameTests.cs`, `WorldThemeTests.cs`.

**Interfaces**
- `WorldTheme.Skin: WorldThemeSkin?`.
- `WorldThemeSkin`: Version, Window, Panels.
- `WorldThemeWindowSkin`: Border, Inset, Overlays, FooterClearance, CompactBelow.
- `WorldThemePanelStyles`: Default.
- `WorldThemePanelSkin`: Border, Inset, HeaderHeight.
- `WorldThemeSkinBorder`: Url, SourceSize, Slice, Thickness.
- `WorldThemeSkinOverlay`: Id, Url, Anchor, Size.
- Core value records: `SkinPixelSize(int Width, int Height)`, `SkinPixelBox(int Left, int Top, int Right, int Bottom)`, `SkinBox(double Left, double Top, double Right, double Bottom)`, `SkinSize(double Width, double Height)`, `SkinFooterClearance(double Left, double Right, double MinHeight)`.
- `WorldThemeSkinJson.Read(JsonElement root)` returns a sanitized optional skin; `Write(Utf8JsonWriter writer, WorldThemeSkin skin)` writes the skin value in deterministic order.
- `WorldThemeSkinJson.CanonicalKey(WorldThemeSkin skin)` returns deterministic known-field JSON (or its SHA-256) for structural comparison. Array instances from a catalog refresh must not trigger repeated reloads if their values are unchanged.

- [ ] Add a full parse/serialize test and unsupported-version fallback test. For the latter, use real fixture data with only skin.version changed:

```csharp
[Fact]
public void UnknownSkinVersionKeepsLegacyFrameAndColors()
{
    var json = JsonNode.Parse(File.ReadAllText(FixturePath))!;
    json["skin"]!["version"] = 99;
    var theme = JsonSerializer.Deserialize<WorldTheme>(json.ToJsonString());
    Assert.NotNull(theme);
    Assert.True(theme.IsValid);
    Assert.Null(theme.Skin);
    Assert.NotNull(theme.Frame);
    Assert.Equal("#15202B", theme.Colors.TerminalText);
}
```

- [ ] Run `dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Release --filter WorldThemeSkinTests`. Missing Skin is the initial expected failure.
- [ ] Implement the types and granular reader using the spec's rules. Add Skin to both the existing Read initializer and explicit Write method. Do not rely on reflection serialization to include it automatically.
- [ ] Add table cases: absent skin; nonobject; version 99; negative/string/infinite-equivalent-overflow metrics; invalid URL; source/slice bounds; duplicate anchors; invalid overlay with good window; invalid panel with good window; invalid window with good panel; unknown keys; excessive overlay count; footer overlap.
- [ ] Check repeated parses produce the same CanonicalKey and a serialize/deserialize cycle preserves known fields. Keep required WorldTheme.IsValid independent of Skin.
- [ ] Run the targeted tests and existing WorldTheme/Frame tests.

## Task 2: Implement bounded resource loading and component readiness

**Files**
- Modify `src/Wandur.Desktop/SessionWorkspace.ThemeImages.cs`.
- Modify `src/Wandur.Desktop/SessionWorkspace.Appearance.cs` only for a stable skin comparison/readiness handoff if needed.
- Reuse `src/Wandur.Core/Discovery/WorldCatalog.ThemeImages.cs`.
- Create `src/Wandur.Desktop/ThemeSkinResources.cs`.
- Create `tests/Wandur.Desktop.Tests/WorldThemeSkinResourceTests.cs`.

**Interfaces**
- Resource keys: `skin-window-border`, `skin-panel-default`, `skin-overlay:header`, `skin-overlay:left-flare`, `skin-overlay:right-flare`.
- `ThemeSkinResources` holds read-only ready images and their raw dimensions; it does not own live panels.
- Existing SessionWorkspace remains bitmap owner. Reused URLs share one decoded Bitmap; disposal occurs once per distinct owned bitmap.
- Renderer readiness depends on both sanitized metadata and verified loaded images.

- [ ] Use the existing fake HTTP-handler/fixture pattern to test one request for a panel image even with three panels, mismatched source dimensions, oversize PNG, broken overlay, and theme A completing after theme B.
- [ ] Implement descriptor enumeration, canonical-URL deduplication, existing URL resolution and stream caps, and the aggregate budgets in the spec. No new raw HttpClient image path.
- [ ] Decode source-sized border PNGs off the interactive rendering path; retain cancellation/stale-result checks and dispose abandoned results. Bound concurrency to two.
- [ ] Keep palette immediate. Adopt valid skin.window atomically when its border is ready; never draw legacy and new window borders together. An optional ornament failure does not gate the base border or panels.
- [ ] On failure use the existing legacy frame loader, with palette-only as final fallback. Do not reserve panel padding while its image is unavailable.
- [ ] Capture the pending Theme object/generation for stale-result suppression even when asset keys are equal; palette-only changes must still apply. Do not repeatedly download because collections were deserialized into new object instances.
- [ ] Run `dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Release --filter WorldThemeSkinResourceTests` and existing ThemeImage/ThemeSwitch tests.

## Task 3: Share nine-slice geometry with explicit destination sizes

**Files**
- Create `src/Wandur.Desktop/ThemeNineSlice.cs`.
- Refactor `src/Wandur.Desktop/ThemeBezelHost.cs` to call it without changing legacy sizing.
- Create `tests/Wandur.Desktop.Tests/ThemeNineSliceTests.cs`.

**Interfaces**
```csharp
internal readonly record struct SkinPatch(Rect SourcePixels, Rect Destination);
internal static class ThemeNineSlice
{
    // Return empty when the destination cannot fit the border at its fixed thickness.
    internal static IReadOnlyList<SkinPatch> Build(
        PixelSize source, SkinPixelBox slice, SkinBox thickness, Size destination);
}
```

- [ ] Add a pure geometry test that distinguishes pixels from DIPs:

```csharp
[Fact]
public void HighResolutionCornersUseDeclaredDipThickness()
{
    var patches = ThemeNineSlice.Build(
        new PixelSize(512, 384),
        new SkinPixelBox(48, 128, 48, 80),
        new SkinBox(24, 64, 24, 40),
        new Size(1380, 900));
    Assert.Equal(8, patches.Count);
    Assert.Contains(patches, p =>
        p.SourcePixels == new Rect(0, 0, 48, 128) &&
        p.Destination == new Rect(0, 0, 24, 64));
}
```

- [ ] Run the new test and observe missing helper failure.
- [ ] Build the source grid from [0, left, width-right, width] and [0, top, height-bottom, height]. Build the destination grid from [0, thickness.left, width-thickness.right, width] and corresponding Y boundaries. Pair cells except center (1,1); discard zero-area cells. Invalid source geometry yields no drawable component.
- [ ] Adapt source-pixel rectangles to the pinned Avalonia bitmap drawing API; cover PNG DPI metadata explicitly rather than assuming PixelSize equals every API's image coordinate system.
- [ ] Add tests for edge-only stretching, no center draw, tiny bounds, fractional display scaling, zero slice margins and source mismatch. Add a rendered marker-bitmap test so corner colors and seams are checked, not only calculated numbers.
- [ ] Legacy host passes its original pixel-sized destination margins through the helper. Keep WorldThemeFrameViewTests green.

## Task 4: Add the window host, ornaments and local footer clearance

**Files**
- Create `src/Wandur.Desktop/ThemeWindowSkinHost.cs`.
- Create `src/Wandur.Desktop/ThemeOrnamentLayer.cs`.
- Modify `src/Wandur.Desktop/MainWindow.cs`, `ThemeService.cs`.
- Create `tests/Wandur.Desktop.Tests/WorldThemeWindowSkinTests.cs`.

**Interfaces**
- Window host chooses one ready window skin or the existing ThemeBezelHost fallback.
- Ornament layer is a noninteractive sibling above the shell, never parent of the shell.
- Host exposes effective `SkinFooterClearance` to the live footer and computes compact mode from its own bounds.
- Applied theme/resource changes use the existing ThemeService.Applied event; attach/detach subscriptions with visual lifetime.

- [ ] Add geometry tests for anchors and independent left/right readiness. For a 1380x900 host, the 120x64 right flare must occupy Rect(1260,836,120,64), regardless of dock visibility.
- [ ] Add separate background and foreground decoration layers around the existing live shell. Use the reusable patch renderer for the border, and fixed destination rectangles for ornaments. Do not draw the center bitmap patch.
- [ ] Implement the fixed anchor math:

```csharp
var x = anchor switch
{
    "bottom-left" => 0d,
    "bottom-right" => hostWidth - width,
    "top-center" => (hostWidth - width) / 2,
    _ => throw new InvalidOperationException()
};
var y = anchor == "top-center" ? 0d : hostHeight - height;
var bounds = new Rect(x, y, width, height);
```

- [ ] Make only the decorative layer IsHitTestVisible=false and nonfocusable. Keep native window controls, header dragging and Dock content interactive.
- [ ] Compute footer padding and minimum height from loaded bottom ornaments. Assert the sample 24-DIP intrusion fits only in the footer. On compact threshold, unload appearance of ornaments and restore ordinary footer metrics without resetting panel state.
- [ ] Keep the existing toolbar once. Test macOS native titlebar space separately from the decorative top band; do not put toolbar controls under the floating header ornament.
- [ ] Test new skin -> legacy -> palette transitions, active-session changes, closed-window subscriptions and asset failure with no stale inset.
- [ ] Run targeted headless tests plus existing WorldThemeFrameViewTests, ThemeSwitchTests and ThemeSwitchContrastTests.

## Task 5: Skin actual Dock tool containers without taking over layout

**Files**
- Create `src/Wandur.Desktop/ThemeDockSkinHost.cs`.
- Create `src/Wandur.Desktop/Styles/ThemeDockSkin.axaml` and include it through `App.axaml`.
- Modify `App.axaml`, `ThemeService.cs` and `Converters/DockChromeConverter.cs` only where skin-scoped resource/template behavior requires it.
- Preserve `WorkspaceFactory.cs`, `WorkspaceFactory.RightColumn.cs` topology.
- Add `tests/Wandur.Desktop.Tests/WorldThemeDockSkinTests.cs`; extend `DockChromeTests.cs`.

**Interfaces**
- A ThemeDockSkinHost wraps one ToolDock's complete live header and body and consumes the shared `skin-panel-default` Bitmap.
- Existing header bindings, template parts and commands remain intact.
- Plain/fallback template retains existing 4-DIP gaps and outer-edge rounding.
- A loaded skin controls its own padding/header metrics, without applying duplicate default borders.

- [ ] Inspect the installed Dock 12.1.0.6 ToolChromeControl and ToolControl templates before editing their structure. Use a skin-scoped control theme or wrapper that retains required PART names and inherited DataContext. Do not guess selectors from another Dock release.
- [ ] Build one full-panel decoration layer behind the combined live header/body. Make the existing header surface transparent only for a ready skin. Preserve title, pin, close, menu, grip and accessibility.
- [ ] Put live header/body inside panel.inset and give the header its declared height. Collapse the whole skin host with its dock. A tab group has one skin, not a border per tab.
- [ ] Add headless tests for both sides, left only, right only and neither; toggle both Map and Channels off and assert no right ProportionalDock or orphan splitter remains. Test restoring either tool.
- [ ] Verify resize, floating and pin/auto-hide states using real existing commands. No decoration remains in the vacated column. Short panels fall back to plain chrome if their controls cannot fit.
- [ ] Pointer tests must operate the live close/pin/grip and composer through decoration. Verify the central document grows when side tools close.
- [ ] Run new dock tests and the unchanged docking/theme contrast tests. Preserve ScriptPanelRailView behavior without moving it.

## Task 6: Complete site distribution and prepare production exports

Execute the sibling site's proposal tasks. Keep its contract example byte-equivalent to the client proposal's example until real measured production exports require a coordinated fixture update.

- [ ] Prepare five PNG exports from the saved source templates, following the spec's slice/alpha/branding requirements.
- [ ] Use simple generated test PNGs in client tests; do not depend on a running site or public MUD.
- [ ] Add static source files only under site src/Wandur.Site/wwwroot/themes/industrial-v2 and validate file/pixel bounds.
- [ ] Implement independent skin sanitizing, import/directory round-trip tests and local asset-route tests.
- [ ] Keep historical frame validation and palette fixtures. Do not change the live directory snapshot wholesale.
- [ ] Finish production export metadata and synchronize example/contract fixtures in both repos.

## Task 7: Cross-repo acceptance and handoff

- [ ] Run from wandur-client:

```bash
dotnet build Wandur.sln -c Release
dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Release --no-build
dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Release --no-build
python3 scripts/generate-localization.py --check
git diff --check
```

- [ ] Run from wandur-site, with its required Docker test dependency:

```bash
dotnet test Wandur.Site.sln -c Release
git diff --check
```

- [ ] Capture offline synthetic UI at 1380x900, 1040x680 and 1920x1080, plus a narrow/asymmetric panel layout. Exercise 100%, 150% and 200% display scales. Review both/left/right/no docks, stacked right tools, no branding, flare alignment, readable status text and clear composer/native buttons.
- [ ] Use the existing WANDUR_CAPTURE_DIR capture convention for the new visual tests. Record any native-platform behavior not exercised; a headless pass alone does not prove macOS traffic-light placement.
- [ ] Recheck against all ten acceptance points in the spec. Record measured production image dimensions, slice coordinates and window/panel layout metrics.
- [ ] Deliver the implemented diff, test results and capture paths. Do not push, deploy, seed production or restart the owner's active app.

## Copy into Cursor

Implement the modular skin proposal in docs/proposals/modular-world-skins.md using this plan. Preserve the existing theme/frame fallback and Dock layout. The new skin is optional decoration; it must not force panels open. Add unbranded frame/panel artwork with fixed-size lower corner overlays and local footer clearance. Use the adjacent JSON as the proposed contract, prepare/measure actual production exports before hosting them, and keep the sibling site validator synchronized. Work locally, use synthetic offline fixtures, verify the four dock combinations and source-pixel versus DIP behavior, and report any remaining unverified native-platform behavior. Do not push or deploy.
