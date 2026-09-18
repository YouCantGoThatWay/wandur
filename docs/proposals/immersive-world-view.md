# Immersive world view and optional scene protocol

Status: future proposal. No implementation is scheduled. Revisit after the current client work is complete.

## Purpose

Give players a stronger sense of being inside a MUD through a persistent, atmospheric 3D view. Exploration and visible interactions should feel approachable while the game's writing, social interaction, commands and combat retain their existing behavior.

The product hypothesis is that a visual place to explore could help newcomers understand and become interested in a MUD. Whether that produces lasting engagement needs to be tested. Graphics alone do not establish that MUDs will regain popularity.

## Intended experience

A player enters a tavern and sees a warm interior, rain through the windows, a bartender and recognizable exits. Text describes the room and carries conversation. The player can look around, select an object to inspect it, interact with the bartender, or walk toward an exit.

Crossing the northern doorway requests the same game action as typing `north`. Selecting the bartender exposes actions supported by that game, such as look or talk. The client waits for the server to confirm an action before presenting it as successful.

Combat remains readable in the transcript and any supported status panels. Ambient visual feedback could accompany it later, but the first version does not attempt to translate combat into real-time positioning, hit detection or detailed animations.

Returning to the tavern should reveal the same recognizable place. Stable architecture, landmarks and art direction matter more than reproducing every sentence as literal geometry. A stylized scene can leave space for the player's imagination.

## Scope and boundaries

- The immersive view is optional and can be docked, enlarged or closed independently of the transcript and 2D map.
- Keyboard commands remain available. Mouse, keyboard navigation and a text-accessible action list should expose the same supported interactions.
- Camera motion and position inside a room are local presentation state. They do not affect game distance, visibility, combat or which objects can be reached.
- Crossing an exit is an authoritative room transition. Locked doors, failed moves, teleports and unusual exits follow the server's result.
- Server-supported scenes may describe room dimensions, exits and visible entities. A scene inferred from prose is illustrative and must not invent actionable exits or hidden information.
- Continuous shared player positions, physics, collision-based game rules and real-time spatial combat would require a separate server feature set and are outside this proposal's first version.

## Levels of server support

| Support level | Client experience | Limits |
| --- | --- | --- |
| Ordinary text MUD | Optional generic scenes using observed descriptions and known exits | Layout and entity placement are illustrative; uncertain room identity limits continuity. |
| Existing structured room data | Stable scenes associated with supplied room IDs, exits and terrain | Room data alone does not define physical dimensions, object placement or reliable entity identities. |
| Optional scene protocol | Authored or procedural geometry, stable entities, explicit interactions and synchronized changes | Requires server adoption or an adapter with sufficient access to game state. |

Support should be incremental. A game could begin by describing a room template and its doors, then add entities and bespoke assets later. A new Wandur server could publish this information directly; existing MUDs could add an optional adapter.

## Protocol direction

Define an open, versioned application protocol for immersive scenes, initially carried through GMCP. GMCP already transports named packages with JSON data and permits game-specific packages. Existing room packages provide useful identity and exit information, but do not by themselves describe a complete 3D scene. See the [GMCP specification](https://mudstandards.org/mud/gmcp/) and [Room package documentation](https://mudstandards.org/gmcp/room/).

The proposed package names below are illustrative and are not existing standards. The naming, schemas and compatibility policy need review before implementation. Keep their semantics independent of the transport so a future server could expose the same model through another negotiated connection if needed.

| Proposed package | Purpose |
| --- | --- |
| `Wandur.Scene.Hello` | Negotiate protocol version and supported features. |
| `Wandur.Scene.Snapshot` | Supply a complete authorized view of the current scene. |
| `Wandur.Scene.Delta` | Update entities, doors, lighting or other changing state. |
| `Wandur.Scene.Action` | Request a declared interaction or exit traversal. |
| `Wandur.Scene.Result` | Accept or reject a request with its correlation ID. |
| `Wandur.Scene.Resync` | Request a fresh snapshot after missing or incompatible updates. |

A server continues supplying its normal text and existing room messages. Scene support is negotiated separately; accepting GMCP does not imply support for immersive scenes. Clients that cannot render a scene retain normal play.

## Information the protocol needs

### Identity and coordinates

Use stable world, room, scene, exit and entity identifiers. Scope dynamic entity IDs to an explicit session or instance when they cannot remain stable. Never use a display name as the identity of an NPC or object.

Distinguish three coordinate spaces:

- Map coordinates describe room topology and the 2D mapper's layout.
- Scene-local coordinates place geometry and entities inside one rendered room.
- Camera coordinates describe the player's local viewpoint.

The protocol must specify units, axis directions, origin, scale and orientation. A room connected north of another room does not thereby have a known physical size or a continuous shared coordinate system. Doorways can join scenes through transitions even when their layouts cannot align physically.

### Scene description

A minimum scene snapshot identifies its room, revision and presentation template. It may include bounds, floor/wall surfaces, exit anchors, ambient lighting, weather, sound references and asset references. Semantic templates such as tavern, forest path or spacecraft corridor provide a low-effort entry point for server authors.

Custom geometry and asset formats should be optional. A client can substitute built-in assets when a format is unsupported or a download fails. Large media belongs in a downloadable asset package or cache, with the protocol carrying references and hashes.

### Entities and actions

An entity describes something the server says is currently visible: an NPC, player, object or environmental feature. It can supply identity, display text, appearance, a scene-local placement and a list of permitted interaction IDs.

The client requests an action against an identified target. The server validates whether that action is still available. A door may have closed or an NPC may have left between presentation and selection. Asset files and scene descriptions must never execute arbitrary client code.

For ordinary MUDs, an explicitly configured adapter can map a visible interaction to a known command. The client must not guess commands for consequential actions from generated prose. Scene actions use the same private-input, login and session checks as other automated command sources.

### Synchronization

Snapshots and deltas carry scene/instance identity and revisions. A delta identifies the revision it follows; missing revisions trigger a fresh snapshot. Reconnection starts a new synchronization session so old acknowledgements cannot mutate the new view.

An action request has a correlation ID and receives a result. Room traversal completes only after the authoritative room/scene update arrives. Acknowledging receipt of `north` is not proof that movement succeeded. Failed movement leaves the player in the original room with the reason visible in text.

The client can animate the approach to a doorway locally, but must not reveal the destination or let the user interact there before the server authorizes it. Timeouts, unexpected room changes and disconnects return the view to a state consistent with the transcript and mapper.

## Assets, generation and continuity

Begin with a small coherent collection of authored room templates, props and materials. This is enough to test whether the experience is useful without making generated 3D content a prerequisite.

Optional generation could later provide decorative textures or artwork. Supplied assets take precedence. Cache generated content using stable scene identity, relevant description/appearance hashes and generator version; replace it only when those inputs change or the user requests regeneration. Temporary combat messages or people entering a room should not rebuild its architecture.

Record provenance: server-authored, configured by the player, or inferred/generated. The [room environment classifier proposal](room-environment-classifier.md) could help select templates, but its output must not determine room identity or game rules.

Persist scene manifests, asset metadata, hashes and cached content through the client's storage interfaces. SQLite remains the default store; large asset collections may justify a managed external asset cache later, with explicit backup and eviction rules. Asset download size, decoded resource use and render complexity need limits before accepting third-party content.

## Client architecture

Keep protocol decoding, scene state, game actions and rendering separate. A validated scene model should feed an optional renderer, while the existing session controller retains authority over commands and connection state. The transcript and mapper should continue operating if rendering fails or the view is closed.

Rendering technology remains an evaluation decision. Prototype embedding and input/focus behavior on Windows, macOS and Linux before choosing an engine. Check docking, display scaling, accessibility, graphics recovery and resource use on modest hardware. Provide reduced motion, adjustable visual quality and independent sound controls.

## Small pilot

Use one cooperating test world with roughly eight to twelve connected locations: a village square, tavern, shop, a few interiors and an outdoor path. Include recognizable NPCs, one door that can reject movement, a nonstandard exit and a server-driven location change. Use a consistent art style and keep combat textual.

The pilot should answer practical questions:

- Can a newcomer find an exit, move, inspect something and start a conversation without coaching?
- Do they understand that the text remains an essential part of the experience?
- Does the visual view increase curiosity and willingness to keep exploring?
- Does returning to a location feel familiar and consistent?
- Can users recover naturally from a locked exit, stale entity or connection interruption?
- Does authoring scene metadata cost a MUD maintainer a reasonable amount of effort?

Compare the experience with the same small world in the text/2D client. Record task completion, confusion, voluntary exploration, return interest and performance. Treat the first sessions as usability evidence, not proof of a market or a revival in MUD popularity.

## Proposed delivery sequence

1. Finish the current client work and revisit whether this remains a priority.
2. Agree on the pilot world, interaction boundaries and a minimal versioned scene contract.
3. Build a disposable renderer/embedding experiment with authored scenes and recorded events.
4. Add the cooperating server adapter and verify movement acknowledgement, entity updates and reconnect/resync behavior.
5. Run newcomer and experienced-player sessions, then decide whether to invest further.

Questions to settle at that point include camera style, scene-authoring effort, the renderer, protocol naming, asset packaging and how much generic rendering is helpful for unmodified MUDs. This document authorizes no 3D implementation and imposes no change on the current 2D mapper roadmap.
