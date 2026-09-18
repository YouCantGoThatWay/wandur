# Directory artwork layout and supplied world themes

User-approved scope: order listing details as title, artwork, tags/population, description, other information; extend the directory REST contract with optional themes and supply a Legends of the Jedi test theme. Apply supplied themes now; a user preference for conditional use is deferred.

## Contract

Keep the backward-compatible `wandur.directory` schema version 2. Add optional `theme` with `version: 1`, `id`, `name`, `variant` (`dark` or `light`), `corner_radius` (0–16), and `colors`. Color keys: `shell`, `panel`, `terminal`, `text`, `muted`, `accent`, `accent_secondary`, `border`, `terminal_text`. All colors are opaque `#RRGGBB`. Unsupported/malformed optional themes are ignored without discarding the listing. No executable markup, external fonts, or asset loading is part of this contract.

## Work

- [x] Server theme enrichment for Legends of the Jedi on cached and refreshed listings; tests and schema documentation.
- [x] Client typed theme decoding, saved profile persistence and exact endpoint lookup for existing saved worlds; retain themes through profile editing.
- [x] Following user correction, apply the selected world's theme to the whole application. Directory/saved-world selections and session-tab switches update shared resources; unthemed worlds restore the saved default. Explicit terminal color overrides win.
- [x] Reorder listing details with larger title, artwork, compact metadata, description and remaining facts/actions.
- [x] Verify malformed-theme fallback, persistence, session selection, visual appearance, API cache behavior; package client.

Shared boundary: server and client independently implement the contract above. Server work owns directory-server and directory schema docs; client work owns C# and UI. Keep artwork cache keys independent of theme data and preserve cached directory age during enrichment.
