# JavaScript Scripting Implementation Plan

**Goal:** Working per-session JavaScript aliases, triggers and timers with an editor and persisted source.
**Architecture:** Injected worker-process runtime, session coordinator, and MVVM editor; no automatic execution.
**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, Jint, resx.
**Spec:** docs/superpowers/specs/2026-09-16-scripting-design.md

- [x] Runtime: implement ScriptContracts.cs, Jint engine/bootstrap, worker request loop, process runtime/factory and --script-worker startup; test real actions, errors, limits and cancellation.
- [x] Persistence and input: atomic .js store keyed by endpoint hash, bounded ANSI line accumulator, tests for endpoint isolation and fragmented lines.
- [x] Session: SessionScripts coordinator; connect lifecycle, command aliases, output triggers, timer tick, private-mode/run-generation gates, loopback regressions.
- [x] UI: AvaloniaEdit syntax highlighting, theme colors, line numbers, indentation and stable undo/caret; ScriptEditorViewModel, nonmodal ScriptsWindow, localized editor/run/stop/save/log/example guidance in five languages, Session menu; editor tests.
- [x] Verify and deliver: full tests, localization facade check, macOS packaging, separate scripting preview and user-facing API documentation.

Contracts are defined in src/Wandur.Core/Scripting/ScriptContracts.cs. UI consumes Desktop.Services.SessionScripts: Source (read/write), Log, Error, IsRunning, IsBusy, CanRun, WorldName, Changed; RunAsync(), Stop(), Save(). Source defaults to a documented starter script. Session coordinator consumes runtime responses; engine never receives desktop objects. Regression tests use a recording factory or real loopback sockets; production DI always supplies implementations explicitly.

## Verification

A clean Release rebuild completed with zero warnings/errors. The full suite passed 228 tests (165 core, 63 desktop), with none skipped. Localization facade validation passed. Packaged macOS worker verified UTF-8 output and alias execution. An isolated native scripting preview verified highlighted source, Run, Save, closing/reopening the editor, and `lh` expanding to `look` in the offline demo. Light/dark editor captures were inspected. Windows/Linux native packaging has not been exercised on those platforms.
