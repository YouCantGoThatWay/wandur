# Expanded mapper implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development; review each component and integrated behavior before delivery.

**Goal:** Deliver a usable editable, persistent 2D mapper with standard and grid views, terrain styling, search and navigation.

**Architecture:** Core owns room graph, editable metadata, history, route planning and validated persistence. Desktop view models own area/floor selection, editor state and viewport; controls render and forward gestures. A session-bound walking coordinator verifies room arrivals.

**Tech Stack:** .NET 10, Avalonia, CommunityToolkit.Mvvm, resx, xUnit/Avalonia Headless.

**Spec:** `docs/superpowers/specs/2026-09-17-expanded-mapper-design.md`

## Global constraints

Preserve existing cache readability and script/login privacy. All five UI languages. No hosted inference or model training in this task. No Git checkout is present, so no commits/worktrees are available.

## Tasks

- [x] Core: extend map models/protocol metadata, manual editing/history, routing and durable merging/import/export; test directed routes, costs/locks, edits surviving observations, deletion persistence and malformed imports.
- [x] Editor: expand map view model and UI for areas, standard/grid mode, search, room/exit editing, palette and notes; tests for actual editing/search/area workflows.
- [x] Renderer: standard graph and contiguous grid, terrain colors/symbols, route/current overlays, coordinate selection and intentional room moves; render real screenshots for both modes.
- [x] Navigation: session-scoped one-step-at-a-time walking with cancellation, timeout, wrong arrival/blocked movement handling and public-input gating; protocol evidence status.
- [x] Integration: localized resources, JSON import/export dialogs, DI wiring, documentation, independent review, full regression tests and app packaging.

## Progress

- Proposal written; implementation design authorized by user request.

- Core suite: 216 passed. Final desktop suite: 125 passed (including 18 loopback navigation cases). Localization facade check passed; fresh desktop rebuild had zero warnings/errors.
- Independent review fixed imported-area selection, grid fitting/stubs, reciprocal door markers, exercise cancellation and rejected-room acknowledgements.
- Rendered fixtures for both modes, route overlay and editor are in `artifacts/screenshots/map-*.png`.
- macOS package rebuilt successfully at `artifacts/macos/Wandur.app`; existing live clients were not restarted.
