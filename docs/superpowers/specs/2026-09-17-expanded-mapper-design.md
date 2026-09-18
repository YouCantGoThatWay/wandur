# Expanded 2D mapper design

User-authorized scope: evolve the existing mapper toward the useful editing/navigation capabilities of Mudlet, including node-and-link and contiguous grid presentation. The supplied images are visual references. Classification is a proposal only, in `docs/proposals/room-environment-classifier.md`.

## Behavior

- Persist areas with independent grid/standard display settings. Floors remain separate. Grid mode fills the entire coordinate cell and hides ordinary exit lines; adjacency never invents connectivity.
- Terrain/environment palettes color room fills. Symbols and notes distinguish features. Selection/current position/uncertainty remain distinct overlays.
- Search rooms by name, description, notes or server ID; select a result to reveal its area/floor.
- Edit room name, description, area, coordinates, terrain, custom color, symbol, notes, traversal cost and route exclusion. Move rooms intentionally in edit mode; add/remove rooms; merge duplicates; undo/redo manual edits.
- Edit directed exits, including custom commands, door state, exclusions and weights. Do not assume reciprocal links; offer explicit return-link creation.
- Plan weighted paths across areas and floors, omitting excluded/locked routes. Show a route preview and allow cancellable walking that sends one command at a time and checks observed arrival before continuing.
- Preserve GMCP/MSDP observation and conservative text recognition. Decode supplied coordinates and environment; retain manual overrides.
- Expose negotiated protocol and received room-field evidence separately. Unknown means unknown, not unsupported.
- Versioned JSON map import/export with bounded validation; legacy caches remain readable. Manual deletions must survive saving and reopening, including stale session saves.

## Implementation constraints

C#/.NET 10, Avalonia, existing DI/MVVM boundaries; UI strings in all five existing resx languages. No model download, remote content loading, or API calls introduced for map rendering. Room identity/topology remain separate from display layout. Keep changes in the current shared workspace; it is not a Git checkout.

This implementation does not promise binary Mudlet `.dat` compatibility, its complete Lua API, 3D display, or game-specific mapping packages. Record supported behavior and remaining differences explicitly in user documentation.

Reference: https://wiki.mudlet.org/w/Manual:Mapper describes grid mode as adjoining room tiles without exit details, and areas as separate coordinate spaces.
