# Unified SQLite Storage Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development; inspect each component and integrated persistence before delivery.

**Goal:** Store Wandur's persistent client data in one cross-platform SQLite database with safe legacy migration.

**Architecture:** A shared database service owns schema/open/transactions and stable world identity. Store implementations keep existing domain interfaces, with an added settings interface and catalog cache seam. UI remains MVVM; injected stores determine persistence. The running map graph remains in memory.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, xUnit, Avalonia.

**Spec:** `docs/superpowers/specs/2026-09-17-sqlite-storage-design.md`

## Constraints

No 3D implementation. Preserve existing files, passwords in OS vault, five UI languages, no live user-data migration in development. No Git repository exists in this workspace.

## Tasks

- [x] Database foundation + schema/world identity + normalized map persistence, legacy import and stale-save tests.
- [x] Settings/profile and script stores with identity-safe address edits, migration and roundtrip tests.
- [x] Catalog/art cache and historical protocol observations, DI integration and migration tests.
- [x] Review integration, run full tests/localization checks, document storage/backup behavior and package app.

Validation: 247 Core tests and 126 Desktop tests passed. `python3 scripts/generate-localization.py --check` passed. The macOS app was rebuilt at `artifacts/macos/Wandur.app`.

## API contract

`Wandur.Core.Storage.ClientDatabase(string path)` exposes `FilePath`, `OpenConnection()` (initialized, foreign keys enabled), `Read<T>(Func<SqliteConnection,T>)`, and `Write<T>(Func<SqliteConnection,SqliteTransaction,T>)` wrapping SQLite errors in IOException. `ResolveWorld(connection, transaction, string endpointKey, string? preferredWorldId=null)` returns a stable string ID, preserving or rejecting conflicting associations. Schema base tables: worlds(id TEXT PK), endpoints(endpoint_key TEXT PK,world_id TEXT), client_settings(id INTEGER PK,payload TEXT), profiles(id TEXT PK,world_id TEXT,payload TEXT), scripts(world_id TEXT,id TEXT,name TEXT,source TEXT,enabled INTEGER, PK(world_id,id)), imports(key TEXT PK). Stores may add their own tables via foundation schema coordination.

Settings interface `ISettingsStore { string FilePath; SettingsLoadResult Load(); void Save(ClientSettings); }`. Existing SettingsStore implements it. SQLite implementations accept ClientDatabase + legacy location. Root owns composition, desktop constructor interface change, directory/art cache and protocol history. Map agent owns foundation/project references/map stores. Settings agent owns settings/script implementations and migration helpers. Review agent reviews read-only after components land.
