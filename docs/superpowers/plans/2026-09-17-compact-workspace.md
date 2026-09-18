# Compact workspace

## Scope

Implement the requested titlebar toolbar, reusable directory browser in empty session tabs, and a map-first panel. Preserve existing connection, browser, map editing, routing, localization and persistence behavior.

## Tasks

- [x] Use Avalonia 11's macOS extended client area with native traffic lights, a compact toolbar and draggable empty space; retain ordinary decorations on other platforms.
- [x] Extract a reusable directory browser control, host it in both the dialog and empty session tabs, and reduce headings, result rows and list width to prioritize descriptions.
- [x] Replace the map's persistent text and tool sections with compact controls and secondary tools; investigate contiguous grid spacing and protect room dragging with a regression case.
- [x] Verify UI behavior, captures, targeted regressions and the full test suite, then package the macOS app.

## Integration boundaries

| Work | Shared boundary | Decision |
| --- | --- | --- |
| Titlebar / browser | MainWindow creates WorkspaceFactory | Browser supplies a control factory/dependency; titlebar task updates both initial and restored layouts. |
| Browser / map | Existing resource dictionaries and generated localization | Coordinator integrates new resource keys and generates once. |
| All | Avalonia application and tests | Keep test fixtures isolated from live data and don't restart a connected client. |

The directory view owns its view model and subscriptions, while the catalog and sessions remain shared services. Map display changes must not rewrite authoritative room coordinates or infer unobserved exits. Existing tools remain accessible through the secondary interface. This workspace has no Git repository, so verification uses the edited source and tests rather than commits or worktrees.

## Progress

- Browser and map tasks delegated independently; titlebar and integration handled locally.
- Saved-map inspection found seven rooms with separated groups and only a reciprocal north/south connection. Grid cells already touch for adjacent coordinates. Preserve the separated positions; coordinate-scale inference was removed because it could erase the visual effect of editing room positions.
- Review found no remaining correctness issues. Browser initial empty-state rendering was corrected, and transition tests now wait for Avalonia to attach the newly displayed terminal before inspecting its visual controls.
- Verification: 247 Core tests and 132 Desktop tests passed; localization generator check passed. Native macOS preview verified window buttons alongside toolbar, dragging, and opening the directory dialog. Headless browser captures checked at 1380 and 1040 widths; map grid and tools overlay captures checked. Built `artifacts/macos/Wandur.app`.
