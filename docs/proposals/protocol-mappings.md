# Shared protocol mappings

## Implementation status (2026-09-18)

The anonymous-probe-first service path is now implemented. Shared contracts live in `Wandur.Models`, which now comes from the `wandur-sdk` submodule at `external/wandur-sdk`; the discovery worker, which handles daily discovery, persistent evidence, Azure generation and publication, moved to the separate `wandur-discovery` repository; the client applies validated mappings to normalized game state. See that repository's worker guide for the actual version-1 wire format, configuration and limitations. The earlier contract examples below are design history, not the implemented wire format. The manual mapping editor and HUD widgets remain future work.

## Goal

Give each world a useful character display without hard-coding every game's vocabulary into the transport or UI. A resource has an ID, a label, a current value and an optional maximum. Other normalized fields cover character identity, level, experience, currency and location. Missing data stays missing. A missing maximum must not produce a fabricated percentage.

The two sources of mappings use the same versioned, declarative format:

1. The directory service distributes validated mappings for a particular world.
2. Players create or override mappings locally using the fields their connection has actually received.

Local overrides win over service defaults. Common built-in mappings can fill remaining gaps. Keep a last-known-good cached version for offline use and rollback. New service versions must not replace a player's choices.

## Implemented foundation

The client requests common GMCP data modules and discovers native MSDP and MSDP-over-GMCP reportable variables. It retains incoming packages independently of whether a gameplay adapter understands them. The Diagnostics field inventory accumulates names and wire types across messages and produces a stable SHA-256 fingerprint. It excludes values and remains local to the connection. Redacted and authentication messages do not contribute to the inventory.

This is an observed schema, not a complete server contract. Partial updates do not remove fields. Array lengths and changing scalar values do not alter the fingerprint. New paths or wire types do. The current inventory preserves dynamic object keys and null types literally, so character-specific maps and newly observed nulls can introduce additional fingerprints. A production service needs per-package aggregation, coverage tracking, debouncing, and rules for known dynamic dictionaries before using these fingerprints as mapping-job triggers.

A limited inventory is marked `limited: true`. It must never be accepted as evidence of a complete or removed server field set. Paths use JSON Pointer escaping, with `*` as an array wildcard and `~2` for a literal asterisk. Root paths are empty strings. MSDP uses package `MSDP` and paths such as `/HEALTH`; MSDP carried over GMCP retains protocol `GMCP` and package `MSDP`.

## Proposed mapping contract

The runtime and service endpoints below are proposed, not implemented:

```json
{
  "schemaVersion": 1,
  "worldId": "mudverse:example",
  "revision": 1,
  "resources": [
    {
      "id": "health",
      "label": "HP",
      "current": {
        "protocol": "GMCP",
        "package": "Char.Vitals",
        "path": "/hp",
        "convert": "finite-number"
      },
      "maximum": {
        "protocol": "GMCP",
        "package": "Char.Vitals",
        "path": "/maxhp",
        "convert": "finite-number"
      }
    }
  ]
}
```

A world mapping should also declare its required GMCP data modules and versions. That lets the client extend the common subscription list for a specific game. Any initial data requests must use an explicit, reviewed protocol request vocabulary, not arbitrary game commands. A mapping must not advertise media, browser or authentication capabilities that the runtime does not implement.

Bindings need explicit update semantics and source priority. Current and maximum values may arrive in separate packages. Keep resource state per character and connection, with timestamps, resets at reconnect/character change, and an explicit stale state. Do not mix sources across GMCP and MSDP without a defined precedence rule. Room identity requires coherent observations; independent room names and IDs must not be joined just because they arrived nearby.

Allow a small conversion vocabulary, such as finite numeric conversion, scaling and explicit enumerations. Do not execute scripts or arbitrary expressions from downloaded mappings. Validate paths, types, ranges, maximums, units, payload sizes and mapping versions before applying anything.

## Service-assisted mapping

The directory service does not yet collect authenticated GMCP/MSDP samples or generate mappings. An anonymous connection can often inspect negotiation and a login screen, but usually cannot obtain character vitals. Useful evidence requires an authorized test account, administrator documentation, or a player explicitly sharing a reviewed diagnostic sample.

Use a world ID plus endpoint, mapping version and an accumulated observed schema as the cache key. Compare field structure and types, not changing HP, player names or inventory values. Stabilize observations across enough activity to distinguish a new schema from an incomplete session. Absence from a partial update is not a removal. Deduplicate equivalent evidence across users, apply a cooldown and budget, and call an LLM only when there are meaningful unmapped or incompatible fields.

An LLM proposes a mapping. Validation against representative fixtures and documented semantics determines whether it can be published. Ambiguous names such as `sp` need context; they might mean stamina, spell points or something game-specific. A matching schema hash cannot detect a field changing meaning or units, so player reports and periodic checks still matter. Cost and reliability need measurements from actual worlds before making promises.

Field names themselves may contain character or item identifiers. Even a values-free schema requires review or sanitization before upload. Never send credentials, login packets, chat or a raw diagnostic history to an LLM by default.

## Local mapping editor

Show the received packages and fields beside the normalized resources. Let the player choose a current-value field, an optional maximum, a label and an explicit conversion. Preview the result against live data and show missing, stale or invalid bindings.

Save changes with the connection dialog's existing Save and Cancel workflow. Include reset-to-service-default and export/import for a reviewed mapping. A manual mapping should work without a directory connection or an LLM account.

## Delivery order

1. Broad discovery and local field inventory, included in this change.
2. Normalized resource state and a small deterministic binding engine with fixture tests.
3. Local field picker and preview, saved as part of connection configuration.
4. Versioned directory mappings and a cache with rollback.
5. Opt-in evidence collection and evaluated LLM mapping proposals.

## References

- [GMCP Core support advertisement](https://mudstandards.org/gmcp/core/)
- [MSDP discovery and reporting](https://tintin.mudhalla.net/protocols/msdp/)
- [MSDP over GMCP](https://tintin.mudhalla.net/protocols/gmcp/)
- [GMCP authentication](https://wiki.mudlet.org/w/Standards:GMCP_Authentication)
