# World Script Library Implementation Plan

**Goal:** Per-MUD event-driven scripts, independent enable switches and a dockable highlighted editor.
**Architecture:** Persistent library owns per-script SessionScripts runtimes. WorldScriptLibrary exposes entries and lifecycle. A viewmodel mediates editor actions; WorkspaceFactory owns document docking.
**Tech Stack:** .NET 10, Avalonia 11, Dock, AvaloniaEdit, Jint, resx.
**Spec:** docs/superpowers/specs/2026-09-16-world-scripts-design.md

- [x] Backend: IWorldScriptLibraryStore + records + atomic persistence and legacy migration; WorldScriptLibrary with per-entry runtimes, persistent enabled state, activation/reload/lifecycle/command/event routing. Tests for multiple scripts, persistence, isolated errors, reconnect, privacy.
- [x] Engine: mud.on line and GMCP events, bounded registrations and structured payloads, tests for ordering/errors/private event forwarding. Preserve existing scripting API.
- [x] UI: docked ScriptLibraryView + viewmodel, per-session header Scripts flyout with enable toggles and Edit action. Named library CRUD, Save/reload, preserve source/caret and originating session, localized five languages. Dock lifecycle and UI tests.
- [x] Completion: immutable Events constants, context suggestions and localized API descriptions, keyboard/lexical/lifecycle tests.
- [x] Verify: clean build, all tests, localization check, packaged worker and separate native preview, documentation.

Ruling: existing user instruction authorizes this correction; no extra design approval is needed. No git repository exists here, so no branch/commit steps apply. Delegate independent engine/backend tasks and review final changes; root implements workspace UI integration.

## Final verification

Clean Release rebuild: zero warnings/errors. Full Release tests: 193 core + 90 desktop = 283 passed, none skipped. Localization facade check passed. Packaged worker verified Events.Line, UTF-8 echo and alias behavior. Separate native offline preview verified docked tabs, independently enabled observer, live room event output, and native Events completion accepted using Tab. Review findings fixed: GMCP multiline JSON, dynamic dock titles, flyout save-error feedback, shared send limits, and bounded completion regexes.
