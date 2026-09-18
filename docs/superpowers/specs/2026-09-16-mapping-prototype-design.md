# Mapping prototype

Approved by the user's request to build the preceding sequence-matching design.

A session owns a room graph and tracker. Valid GMCP/MSDP room IDs anchor identity; without IDs, retain multiple candidate locations and narrow them using observed room features and traversed directed edges. Do not identify rooms solely by description hashes. Successful room observations, not commands alone, advance tracking. Explicit movement failure keeps location. Unexpected movement starts relocalization; unresolved observations remain uncertain and never merge known rooms.

Core has no Avalonia dependency. Protocol decoders produce RoomObservation. Text fallback recognizes conservative room/exits blocks and supports LOTJ directional exit lines and the existing offline demo. It is a prototype parser, not universal game understanding. Stable name/description/exits are evidence; transient occupants are excluded from room description where recognized. Store directions and directed links independently of display coordinates. No inferred reciprocal edges.

A dockable MVVM panel follows active session, renders orbitable gray room boxes/exit markers with current/candidate/provisional colors, and displays localized tracking state. A clearly labeled offline exercise supplies a known graph with repeated room descriptions and steps through ambiguity/resolution without sending network commands. Existing live sessions are not automatically navigated. Reset location keeps learned graph. Map persistence is scoped by server endpoint and atomically written, retaining graph on reconnect. Generated textures and LLM extraction remain later work.

Tests cover protocol fragmentation and malformed IDs, ambiguous sequence resolution, failed moves, identical adjacent rooms, directional links, session isolation, persistence, and rendered panel. Initial resources remain English, Spanish, French, German and Brazilian Portuguese.
