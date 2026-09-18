# Wandur 2D mapper

Open the Map panel from the View menu. Maps belong to a stable Wandur world identity and are saved in `wandur.db`. Sessions for the same world retain edits and deletions when their cached maps are merged, including when the world profile's hostname changes.

The panel gives its space to the map. Its compact toolbar provides floor arrows, centering, fitting and **Map tools** (☰). Open Map tools for area/grid settings, routes and protocol diagnostics. Choose **Open map editor…** to open a separate editor document beside the session and script tabs.

## Reading the map

- North is up. Choose an area and use the floor arrows to inspect levels independently.
- Standard mode draws colored room nodes and directed connections. A one-way exit has one arrow; inferred connections are dashed. Door marks distinguish open, closed and locked exits.
- Small teal lights mark known exits on room edges and diagonal corners, in both normal and grid views, including unmapped destinations. Up/down lights sit off-center on the top/bottom edges and contain small chevrons when zoom permits. Links marked closed or locked use amber lights. A light confirms an exit exists; it does not by itself establish a route to another room.
- **Grid mode** is saved per area. Toggle it with the grid icon on the map toolbar or in map tools. Rooms fill their coordinate cells with terrain colors; grid lines follow cell edges at every zoom level. Ordinary connection lines disappear. Touching cells do not imply a known exit or a walkable route.
- Empty cells preserve unknown space and separated room groups. Grid mode does not pack disconnected rooms together or change their saved coordinates; games whose coordinates use a different scale may need an adapter or manual layout.
- Terrain comes from room metadata or your edits. A separate position marker, selection outline and uncertainty indicator keep location evidence distinct from terrain.
- Symbols and notes annotate rooms. Hover to see the room name, terrain, coordinates and notes. Click a room to select it. Cross-area/floor badges inspect the destination without sending a game command.
- Drag the canvas to pan. Use **− / +**, the scroll wheel, a macOS trackpad pinch, or a touchscreen pinch to zoom. Wheel and pinch zoom keep the point beneath the pointer/gesture steady. Use **Fit floor** for an overview or **Center** to return to your current room.

## Search and edit

Open **Map tools → Open map editor…**. The editor has its own map canvas and a resizable inspector for room/exit editing, search and import/export. Drag its document tab out to float it in a separate window. Opening it again selects the existing editor for that session.

The editor's **Search and edit** section searches room names, descriptions, notes, internal IDs and server IDs. Selecting a result reveals its area and floor. The editor and live map have independent viewport and selection state, while saved edits update their shared map immediately. Closing the editor leaves the connection running. Closing the owning session or replacing its map closes its editor; a disconnected session can still be edited.

Enable **Edit map** to move rooms by dragging them onto coordinates. The room editor can add, rename, delete or merge rooms and change their area, coordinates, terrain, color, symbol, notes, traversal weight and route exclusion. Terrain presets provide a starting palette; explicit colors override the palette. Manual corrections survive later observations. Currently a manual edit freezes the editable display fields as a group; incoming identity evidence is retained separately. Undo and redo apply to the last 50 graph edits, including import.

The exit editor edits a directed exit's destination, direction, command, traversal cost, door state and route exclusion. A return exit is created only when explicitly requested. Exit cost zero uses the destination room's positive cost. Custom exit commands are stored for route planning; automatic walking currently accepts direction commands only.

## Routes and walking

Select a destination and expand **Routes**. Plan a route to see its commands and total cost. The pathfinder uses directed connections and weights, excludes closed/locked doors and excluded rooms/exits, and normally excludes inferred connections and provisional rooms. You can opt into inferred routes for preview.

In the live map, **double-click a room** to plan and immediately walk a verified route. A single click still selects the room. Double-click walking is disabled in edit mode and the local exercise. A **Stop** square appears on the toolbar during a walk; progress or an unavailable-route explanation appears above the protocol status. Double-clicking another room during a walk does not replace that walk; stop it first.

**Walk route** requires a connected session and confirmed server room IDs for the entire route. Wandur sends one direction and waits for the expected room ID before sending another. Stop cancels future moves. Wrong rooms, blocked moves, timeout, disconnect, private input, automatic login, another command, or a changed graph stop the walk. A command already sent to the game cannot be recalled.

Connections are also learned directly from movement: leaving room A with `south` and arriving in room B records A → B, even if the server supplies only exit flags. Walk back once to confirm B → A; reverse exits are not assumed. Repeated metadata for the origin does not consume a pending move or acknowledge arrival.

A protocol room change updates your location even when you sent no direction (for example, a teleport or moving transport). It does not create a traversal edge from the previous room. New rooms without known topology or server coordinates start a separate map section; revisiting a known room preserves its existing connections. A subsequent non-direction command clears any older pending direction, so commands such as `enter portal` cannot inherit it.

When server IDs become available for older text rooms, a unique compatible match with the same complete nonempty exit set is upgraded instead of keeping disconnected duplicates. Ambiguous matches remain separate. Saved labels, notes, positions and route restrictions survive the upgrade. Traversing an existing edited connection confirms its evidence without changing its command or restrictions. Identity aliases and confirmed connections persist in SQLite, including across stale session saves.

## Checking GMCP / MSDP support

GMCP room IDs accept `num`, `id`, and `vnum` (numeric or string). `planet` is an area fallback after `area` and `zone`. LOTJ's `vnum`/`planet` payload uses `O` and `C` exit-state flags; these record known directions without inventing destination room IDs. Destination connections are learned when you move between identified rooms. These flags are not currently persisted as door-state metadata; amber lights reflect the map link's stored closed/locked state.

The compact status bar below the map displays separate GMCP and MSDP negotiation statuses. Hover over it for the full status and received room fields; these details also remain in **Map tools**:

- **Supported:** the server accepted that Telnet option.
- **Declined:** the server explicitly rejected or disabled it.
- **Not negotiated:** no answer has been observed; this is not proof of non-support.

Below that, **Room fields received** shows which structured room fields actually arrived during this connection: ID, name, description, exits, area, terrain, coordinates or symbol. Cached or imported rooms do not count as evidence. A server can support GMCP for character vitals without supplying room IDs. Room data may arrive only after login or after moving/looking.

Structured room updates take precedence over guessed text titles even when they contain names/exits without IDs. Text arriving just before the first structured update can be reconciled with that update when it created a new provisional room within two seconds and no command or observation intervened. This prevents nearby login/tutorial headings from becoming extra rooms. A saved matching room is reused instead of duplicating it.

Without room IDs, Wandur uses names, descriptions, exits and sequences of observations conservatively; repeated rooms may remain uncertain. An observed out-and-back between uniquely matching, distinctive rooms records an inferred return exit. It does not assume a reverse exit before you actually use it, or carry that evidence across manual repositioning. Server coordinate/terrain layouts vary, so some games will need a dedicated adapter.

## Import and export

Use **Import map…** and **Export map…** in the editor for versioned Wandur JSON maps. Legacy raw Wandur snapshots are also accepted. Imports are validated before replacement, undoable, and clear current-position certainty; recheck your position afterward. Files are bounded to 16 MiB, 10,000 rooms and 60,000 exits.

This is Wandur's JSON schema, not Mudlet's binary `.dat` format. Custom exit line points are supported in JSON and rendered in standard mode; a graphical line-bending editor is not included.

## Current boundaries

This implementation covers the core 2D mapping workflows illustrated by Mudlet: colored rooms, grid areas, floors, editing, directed routes, metadata and verified walking. It does not provide complete Mudlet API/package compatibility, arbitrary map labels/images, a full drawing editor, or game-specific mapper packages. Text-only route preview is available, but automatic walking requires structured server IDs.

The optional local terrain classifier is documented in [the classifier proposal](proposals/room-environment-classifier.md). It is not trained or enabled by this implementation; room prose is not sent to a model.
