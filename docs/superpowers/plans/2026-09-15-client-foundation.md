# Wandur Client Foundation Implementation Plan

> Execute inline using superpowers:executing-plans, with test-driven development for protocol and stateful behavior.

**Goal:** Deliver a runnable open-source desktop MUD client with real connections and an offline demo.

**Architecture:** Keep byte parsing, session state and persistence in an Avalonia-free core. Compose the desktop UI from independent views hosted by Dock. Both demo and network connections implement the same session interface.

**Tech Stack:** .NET 10; Avalonia 11.3.22; Dock 11.3.12.1; xUnit.

**Spec:** `docs/superpowers/specs/2026-09-15-client-foundation-design.md`

## Global constraints

- MIT license; one active session; no automatic external connections.
- Bound untrusted text and Telnet subnegotiation buffers.
- Do not save passwords or command history; atomic settings replacement.
- UI code stays out of Wandur.Core.
- Future WHO/scripting/mapping features are documented, not simulated as implemented.

## Tasks

- [x] 1. Establish projects and protocol regression tests. In `tests/Wandur.Core.Tests`, exercise fragmented IAC, negotiation, UTF-8, SGR, carriage-return overwrite, backspace, and history draft/private input. Use hand-derived text/color and byte expectations. Implement corresponding `Protocol`, `Terminal`, and `Sessions` types under `src/Wandur.Core` only after observing failures. Run `dotnet test tests/Wandur.Core.Tests`.
- [x] 2. Add `ConnectionProfile`, `ClientSettings`, and `SettingsStore` with round-trip/corruption tests. Add `IMudSession`, `TelnetSession` and `DemoSession`; test command CRLF framing and remote disconnect against a loopback listener, then demo movement and invalid movement. Run core tests.
- [x] 3. Add `App`, `MainWindow`, `WorkspaceFactory`, `TerminalView`, profile/options dialogs and theme service in `src/Wandur.Desktop`. Wire cancelable async connection lifetime, private command entry, selectable bounded output, history navigation, save transcript, quick commands and docking reset. Build desktop project and resolve API integration issues against package documentation.
- [x] 4. Add concise README, contributor instructions and CI build/test matrix. Validate Release build, tests, demo interaction, settings application and persistence, disconnect/reconnect, private history exclusion and actual floating panels. Save a preview image if possible. Record exact test results and current feature boundaries.
