# Wandur client foundation

The user approved starting the independent, open-source C# MUD client after the feasibility discussion. This first increment is a runnable desktop client, not the server project. Working name: Wandur. License: MIT.

## Product

A quiet, readable desktop workspace with a world library, central selectable ANSI transcript, command entry, quick commands, connection status, and real movable/floating tool panels. Provide an offline demo adventure for immediate exploration. Real connections use user-entered host/port and optional TLS; no external server is contacted automatically.

## Architecture

- .NET 10 and Avalonia 11.3.22, pinned; Dock 11.3.12.1 for docking.
- Wandur.Core: independent of Avalonia; streaming Telnet parser, ANSI transcript model, command history, connection lifecycle, profile/settings persistence, and deterministic demo session.
- Wandur.Desktop: window composition, docking, transcript renderer, dialogs and theme resources. Own UI changes on the UI thread. No server-specific networking assumptions.
- Wandur.Core.Tests: fragmented streams, real local TCP integration, buffer limits, history privacy, persistence and demo behavior.

## Required behavior

One active session in this increment. Connect/cancel/disconnect/reconnect without blocking UI or leaking old output. Decode split UTF-8 sequences correctly. Handle Telnet IAC escaping, ECHO, SGA, TTYPE, NAWS and GMCP negotiation; reject unsupported options, including compression. Respect split ANSI SGR (16/256/true color), carriage returns, backspace and partial-line prompts. Bound transcript memory. Keep scroll position when reading history.

Save connection profiles and appearance settings as JSON under the user's application-data directory. Never persist passwords or command history. Validate host, port, encoding, theme colors and font size. Atomic settings replacement; corrupt files must produce a visible recovery notice without silently losing the original.

Provide private input (masked and excluded from local echo/history), with automatic activation for server ECHO negotiation and common password prompts. No automatic login. Four palettes and configurable text size. Save selectable transcript on explicit request. Dock layout can be reset; cross-restart layout persistence follows in a later increment.

## Scope boundaries

WHO polling, regex profile editing, Python scripting, macros, room inference, 3D mapping and generated textures remain separate future increments. Do not fill their panels with invented live data. Demo content is explicitly labeled and never mixed with a real server.

## Validation

Run tests for byte-stream fragmentation and connection teardown using a loopback TCP server. Run Release build. Exercise the desktop window with the offline demo, a refused connection, settings and docking. Use headless UI checks where helpful and native inspection when available. Report untested platform coverage honestly.
