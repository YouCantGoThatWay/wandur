# Dedicated terminal and Avalonia 12

Approved by the user: integrate Iciclecreek.Avalonia.Terminal and upgrade Avalonia where dependencies permit. Keep the separate command box, login privacy, scripts, maps, localization, and theme palette.

Use Avalonia 12.1.2, Dock 12.1.0.6, AvaloniaEdit 12.0.0, and Iciclecreek 4.0.2. A DI-provided factory creates one display per session. Feed incremental server/local/reset notifications from the existing transcript model, retaining the model for export and existing parsing. The display owns its bounded screen/scrollback across tab detachments; session disposal releases it. No PTY or shell is launched.

The adapter owns local echo isolation, default-off text blinking independent of command focus, palette resource updates, and scrollbar/tail following. Preserve split escape sequences without allowing local echo to complete them. Server output cannot access the system clipboard, launch programs, or resize windows through the embedded control.

Verify cursor movement, erase, split controls, local echo, clear/reconnect, inactive tabs, palette changes, literal RGB colors, blink rendering, and login/script regressions. Run existing tests and package the macOS app. Record measured workload timing without presenting it as a general throughput guarantee.
