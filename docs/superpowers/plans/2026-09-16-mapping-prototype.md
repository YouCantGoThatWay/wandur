# Mapping Prototype Implementation Plan

**Goal:** Build a usable dockable map with authoritative IDs and sequence-based fallback.
**Architecture:** Core observations/graph/tracker; Telnet protocol adapters; per-session controller coordination; MVVM view and projected 3D renderer.
**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, resx; no new renderer runtime dependency.
**Spec:** docs/superpowers/specs/2026-09-16-mapping-prototype-design.md

## Global constraints
- Preserve user sessions; do not move player for testing.
- All new UI strings in five resource files.
- No credentials or full transcripts in map cache; endpoint-scoped graph only.
- Unknown locations stay uncertain; never silently merge identical rooms.

## Tasks
- [x] Protocol adapters: add tests with literal GMCP and MSDP payloads, fragmented Telnet negotiation, malformed inputs; normalize to RoomObservation and advertise supported room packages.
- [x] Core tracker: test a graph with identical corridor rooms; Observe with north/east history must reduce candidates to the matching path. Failed movement keeps current room. Persist and reload without losing edges. Implement bounded candidate tracking and conservative learning.
- [x] Text observation: feed split colored demo/LOTJ transcripts and assert name/exits; unrelated chat and password prompts produce no observation.
- [x] Desktop: per-session map controller, structured-data priority, pending movement coordination and cache. View model + dockable gray-box renderer with orbit/zoom/reset and offline ambiguity exercise.
- [x] Verify complete solution and screenshots, build macOS bundle, document prototype limits and reproduce demo progression.

Independent protocol and UI units run in parallel under dispatching-parallel-agents; root owns graph/parser/controller integration and final verification. No git repository is present, so no commits/worktrees.

Verification: final suite 130 core + 42 desktop tests passed. Five-language resource facade check passed. Headless exercise screenshots inspected. Native preview is isolated from existing live sessions.
