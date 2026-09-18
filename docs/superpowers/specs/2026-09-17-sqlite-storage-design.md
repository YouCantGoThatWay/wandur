# Unified SQLite storage

## Approved direction

Use a local `wandur.db` behind injected storage interfaces. Preserve the current cross-platform UI, mapper, scripting and login behavior. Import existing JSON/script files automatically; retain originals. The 3D protocol discussion is out of implementation scope.

## Identity and data

Worlds have stable IDs. Endpoints resolve to world IDs; saved profiles retain their existing IDs, credential references and association when an address changes. Profiles sharing an endpoint share maps and script libraries. Do not merge unrelated worlds automatically if an edited endpoint already belongs to a different world; reject that conflicting edit.

Store settings/profiles, scripts and enabled flags, normalized room/exit rows, area settings, aliases/deletions and directory/artwork cache in one SQLite database. Preserve all room/link fields using per-row payloads plus indexed identity/coordinate columns. Graph rendering and route finding remain in memory. SQLite transactions serialize storage mutations; existing graph revision/deletion rules remain responsible for stale-session conflicts.

Current session protocol evidence stays distinct from persisted historical observations; store last-known endpoint capabilities with observation timestamps. No transcript/password capture is introduced. Passwords remain in OS credential vaults.

## Migration and failure handling

Use schema versions and transactions. Legacy import is idempotent, validates source data and never overwrites newer database data on restart. Known endpoints migrate eagerly or before their first mutation. Preserve orphan files when their hashes cannot identify the original endpoint. Import such data when its endpoint is next used. Leave original files untouched as migration backups. Surface errors instead of silently replacing corrupt data. Never modify the user's live data during development tests.

SQLite may create journal/WAL sidecars while running; the application has one logical database, not an assurance that copying an open file is a consistent backup. Use SQLite backup APIs for future live backup tools.

## Verification

Test first import/restart idempotence; all fields and script-enabled flags survive; host edits preserve identity; profile duplication shares world data; stale map saves preserve deletions/overrides; rejected migrations do not lose originals; directory/art cache works offline; real app DI resolves all SQLite implementations; existing regressions pass. Package the app after verification without restarting the user's live client.
