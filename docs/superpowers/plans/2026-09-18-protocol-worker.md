# Shared protocol models and daily discovery worker

**Goal:** Share game-neutral contracts between the C# client and a C# discovery worker, discover anonymous GMCP/MSDP fields daily, and distribute validated Azure-assisted mappings through the existing directory API.

**Architecture:** `Wandur.Models` has no framework/package dependencies and owns normalized game state, protocol evidence, mapping documents and validation. `Wandur.Core` consumes it and supplies the existing transport and a deterministic binding engine. `Wandur.Discovery.Worker` references both, persists evidence and last-good mappings, and runs once or daily. Python continues serving the directory and overlays a worker-owned mapping sidecar without changing upstream cache age or artwork.

**Spec:** `docs/proposals/protocol-mappings.md`, updated with the approved anonymous-probe-first approach.

## Constraints

- .NET 10, warnings as errors; no rewrite of the directory or GUI.
- No login, gameplay commands, raw transcripts or credentials collected by probes.
- General entities (character, opponent, vehicle, world), categories and named resources; no Jedi-specific properties.
- Finite bounded conversions only, no downloaded executable expressions.
- Advertised variable sets and observed paths/types are separate evidence. Hash stable sorted structure, not values. Incomplete observations cannot remove prior fields.
- Preserve last-good mappings on failures. Mapping schema/prompt revisions participate in regeneration decisions.
- Mapping calls use a separately configured Azure text deployment; never assume an available model name. Budget and concurrency limits apply.
- Directory is untrusted input: reject nonpublic endpoints in production and bound probe time, bytes and events.

## Tasks

- [x] Add shared contracts, validation and serialization in `src/Wandur.Models`; test malformed mappings, legitimate Jedi/generic resources, and stable evidence fingerprints.
- [x] Add C# worker with anonymous probes, persistent daily scheduling, change detection, deterministic known aliases and Azure proposals for unmapped fields. Test local TCP negotiation and injected HTTP responses, cache reuse, failed mapping retention and cancellation.
- [x] Add the independent Python sidecar overlay with endpoint validation and tests. Sidecar: `cache/protocol-mappings.json`, `{schema_version:1,worlds:[mappingDocument]}`. Mapping document: `schema_version`, `world_id`, `endpoint:{host,port,use_tls}`, `schema_fingerprint`, `revision`, `generated_at`, `provenance`, `bindings`. Directory field: `protocol_mapping`.
- [x] Consume mappings in Core with a bounded deterministic state mapper and directory/profile caching; test incremental resources, invalid numbers, reset and missing maxima. UI widgets and manual mapping editor remain separate work.
- [x] Document configuration and operation, run backend and complete .NET checks, verify a bounded live Jedi probe, configure daily execution without exposing secrets. Azure deployment configured and verified; local daily launch agent installed.

Each behavioral task starts with failing tests, then implementation and targeted verification. Final checks: release build, complete solution tests, backend unittest discovery, localization generation check. Workspace is not a Git checkout, so no branch or commits are created.
