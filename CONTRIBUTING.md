# Contributing to Wandur

Small, usable improvements are welcome. Start with an issue explaining the player experience or compatibility problem you want to improve.

- Keep server-specific parsing in profiles/adapters; the transport and GUI must remain independent of a particular game.
- Keep Avalonia dependencies out of Wandur.Core.
- Treat incoming text and protocol data as untrusted. Bound buffers and use timeouts for configurable regexes.
- Do not store passwords, private commands or API keys in profiles, logs, fixtures or screenshots.
- Add regression tests for protocol, connection lifecycle, persistence and privacy changes. Use a local TCP fixture instead of relying on a public MUD.
- Test visible behavior for UI changes. Avoid asserting cosmetic implementation details.
- Run `dotnet build Wandur.sln -c Release` and `dotnet test Wandur.sln -c Release` before submitting a change.

Dependencies are pinned and lockfiles are checked in. Update packages deliberately and include the regenerated lockfiles. CI restores with `--locked-mode`.
