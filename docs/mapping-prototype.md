# 2D mapping prototype

Open **View → Map Panel**. Drag the panel header to move or float it. The map is a north-up 2D view: drag to pan, scroll to zoom, and click a room tile to inspect its name and description. Room names appear when there is space without covering tiles or routes; zoom in or select a room for details.

Only one floor is drawn at a time. Use the floor arrows above the map to browse known floors. An ↑/↓ badge above a room identifies a route to another floor; clicking the badge changes the displayed floor and never sends a game command. Vertical exits without known destinations show ↑? or ↓?. Floor numbers come from the existing illustrative Z coordinates, not necessarily the game's own floor names.

**Fit floor** shows the floor's extent and adapts to dock resizing. **Center on position** returns to the current room and its floor at normal zoom (or centers the displayed floor when the location is unknown). Floor browsing persists across repeated room reports; an actual change in the player's floor switches to that floor. Dragging or zooming leaves fit mode. Very large maps may require panning at the minimum zoom.

Mint marks the current location, amber question marks identify candidate rooms, and a white outline marks selection. A solid center dot means a server-confirmed position; a hollow dot and dashed outline mean an inferred position. Provisional room outlines are also dashed. Arrows connect mapped destinations (two arrowheads only when both directed routes are known). Each direction retains its own solid/confirmed or dashed/inferred evidence. Dotted exit stubs have no visited destination yet. Coordinates illustrate the graph; they are not measurements of the world.

**Try recognition exercise** is local and never sends game commands. Four identical corridors initially fit the observation. Advancing north reduces the candidates to two, then one; another north reaches a distinctive beacon. **Live map** returns to the untouched session map. **Recheck position** discards the location hypothesis while preserving rooms and links.

## Identification

- GMCP: negotiate `Core.Hello` and `Core.Supports.Set`, decode `Room.Info` / `room.info`, and prefer valid numeric/string server IDs. Room names, descriptions, zones, exits and destination IDs are normalized. Sentinel/missing IDs are not authoritative.
- MSDP: discover room variables and prefer a compound `ROOM` table. Flat reports are accepted only when ID and name occur in the same payload. Independent partial fields are intentionally not stitched together, since this could associate an old name with a new ID.
- Text fallback: bounded ANSI-aware parsing of English `Exits: north east` blocks and LOTJ's `Obvious exits:` followed by directional lines. It needs a plausible title and description. This is not a universal room parser; arbitrary formats, brief-only output and non-English game text may need adapters. Interface language does not change the game parser.
- LOTJ trailing `[Hotel]`, `[HOSPITAL]`, and `[ENGINE]` flags are ignored when matching text titles to untagged protocol names. Other bracketed identifiers remain significant. This prevents new duplicate rooms; existing cached duplicates are not automatically merged.
- Inference retains candidate paths, narrows them with observed directed transitions, compares normalized names, description word overlap, areas and available exit evidence. Unexplored links into lookalike known rooms require further distinct observations. New adjacent identical rooms are kept separate. No reciprocal edge is assumed.
- Successful observations advance the marker; directions alone do not. Common blocked movement messages preserve location. Unacknowledged multiple movement commands invalidate location rather than invent a route.

## Boundaries and limitations

The renderer is a 2D Avalonia room-and-connection view. The previous 3D presentation is retired; graph recognition and saved-map formats are unchanged, and a future alternate view can reuse them. Generated textures, LLM extraction, editable maps, calibrated probabilities, active exploration and speedwalking are not part of this prototype.

Room graphs are limited to 10,000 nodes / 60,000 edges and candidate paths to 256. Exceeding candidate capacity reports unknown location instead of selecting from a truncated set. Indistinguishable sequences can remain unresolved. Parser errors, hidden exits, moving rooms, procedural instances and unusual movement commands can still confuse inference; inferred locations are labeled accordingly. Switching from text inference to server IDs can currently leave provisional duplicate rooms; automatic reconciliation is deliberately deferred. False links can require a fresh map cache in this prototype; graphical graph repair is not implemented yet.

Map caches are keyed by normalized host and port, written atomically under the application's `maps` directory, and restored without assuming the player's current location. Session trackers are independent; saves merge discoveries from tabs sharing a server. Cache storage is injected through `IRoomMapStore`; core tracking and protocol code contain no UI dependencies. The view model owns selection, exercise mode, status and floor/viewport state, while the control only renders geometry and forwards gestures. UI resources cover all five supported languages.
