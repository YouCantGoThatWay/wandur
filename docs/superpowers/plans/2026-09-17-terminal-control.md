# Terminal Control Implementation Plan

**Goal:** Replace transcript text layout with an incremental terminal display and upgrade Avalonia.
**Spec:** ../specs/2026-09-17-terminal-control-design.md
**Architecture:** Session-owned display adapter created through DI; existing transcript emits server/local/reset deltas. Desktop view hosts the terminal and keeps command input.

- [x] Resolve compatible package versions and migrate removed Avalonia APIs.
- [x] Add tests for incremental output and local echo boundaries before implementing stream notifications and adapter.
- [x] Wire factory through composition root, session workspace, and controller; retain terminal state when switching tabs and dispose with session.
- [x] Bind theme palettes; add localized default-off blink preference and render tests.
- [x] Migrate affected view tests, verify full suite and screenshots, record timing, package app and document limitations.

Testing uses real terminal buffers and headless rendering. Existing login and scripting tests remain acceptance checks. No shell process is attached. Keep all UI strings in locale resources. No Git repository exists here, so commits/worktrees do not apply.
