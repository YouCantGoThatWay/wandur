# Daily protocol discovery

Wandur.Discovery.Worker is a .NET 10 executable that shares Wandur.Models with the desktop client and reuses Wandur.Core's Telnet parser. The existing Python directory API remains in place.

Run from the workspace root:

```sh
dotnet build Wandur.sln -c Release
dotnet src/Wandur.Discovery.Worker/bin/Release/net10.0/Wandur.Discovery.Worker.dll --once
```

Without `--once`, the worker stays running and checks persisted daily eligibility every five minutes. `--world mudverse:509` selects one world. `--limit 10` selects the first ten connectable listings. `--force` bypasses probe age, but not model budgets or unchanged-schema caching. `--no-ai` collects evidence while preserving existing generated mappings. `--cache-dir PATH` defaults to `directory-server/cache`. A process lock prevents two workers sharing this cache.

## Install the local daily job

On macOS, after configuring `.env`, run:

```sh
python3 scripts/install-discovery-worker-macos.py
```

This publishes the worker to `artifacts/discovery-worker` and registers the user launch agent `net.wandur.discovery`, with an initial run and a 24-hour interval. It runs while the Mac is available and requires the workspace volume to be mounted. The per-world timestamps and process lock still apply. Logs are in `~/Library/Logs/Wandur/`. Re-run the installer after worker code changes. After changing `.env`, the next invocation reads the new settings.

To stop the installed job:

```sh
launchctl bootout gui/$(id -u)/net.wandur.discovery
```

For a server deployment, schedule the same `--once` executable daily using the host's service scheduler, or run its built-in continuous mode under process supervision.

## Configuration

The worker reads environment variables first, then `.env` beside the cache directory. It reuses the existing Azure key and endpoint unless mapping-specific overrides are supplied. No secrets are written to logs or discovery evidence.

| Variable | Default | Purpose |
| --- | --- | --- |
| `WANDUR_MAPPING_MODEL` | unset | Azure deployment name; unset disables paid generation |
| `WANDUR_MAPPING_ENDPOINT` | `AZURE_OPENAI_ENDPOINT` | HTTPS Azure resource endpoint |
| `WANDUR_MAPPING_API_KEY` | `AZURE_OPENAI_API_KEY` | Optional separate mapping credential |
| `WANDUR_MAPPING_REASONING_EFFORT` | `low` | Model effort; empty omits the parameter |
| `WANDUR_MAPPING_DAILY_CALL_LIMIT` | `20` | UTC daily maximum HTTP attempts, persisted before requests |
| `WANDUR_MAPPING_REQUEST_INTERVAL_SECONDS` | `15` | Minimum interval between model request starts |
| `WANDUR_DISCOVERY_CONCURRENCY` | `8` | Concurrent endpoint probes, range 1–16 |
| `WANDUR_DISCOVERY_PROBE_SECONDS` | `15` | Total connection, negotiation and observation deadline, range 2–60 |

The Azure adapter uses `/openai/v1/chat/completions`, sends the deployment name as `model`, and accepts the resource root, `/models`, `/openai/v1`, or a copied Responses endpoint as configuration. It normalizes the path to the API it implements. The image deployment configuration is independent. Model requests use JSON mode, no tools, an 8,192 completion-token cap and a bounded schema-only prompt. Runtime validation is authoritative; JSON mode alone is not semantic validation.

The local first pass can temporarily raise the daily call limit to cover the initial directory, then return it to 20. Quota (TPM) is a throughput allocation, not a spending budget. Attempts, input/output tokens and cached input tokens are recorded in worker state. The token totals are cumulative provider-reported usage, not an invoice or guaranteed dollar charge.

## Shared models

`Wandur.Models` contains plain C# records and contract validation, with no dependency on Avalonia, HTTP clients, SQL or an AI SDK:

- `GameState` has character, opponent, vehicle and world entities.
- Each entity has identity, resources, progression, attributes, currencies, metrics and location collections.
- `ResourceState` keeps current and maximum observations separately. Missing maxima stay missing; no percentage is fabricated. Observations have timestamps and can be marked stale by a consumer.
- `WorldMapping` binds exact protocol/package/JSON Pointer sources to constrained entity/category/key/member targets. Named keys are extensible: `health`, `jetpack_fuel`, `engineering` or another game's vocabulary.
- `ProtocolEvidence` separates advertised names from observed wire types. Only public server identity is retained as a scalar; it is omitted from model requests.

A mapping is scoped to a world and exact host/port/TLS endpoint, carries a revision and evidence fingerprint, and remains provisional. Structural validation does not prove semantic correctness. Clients cache mappings in saved connections, reject malformed optional maps without discarding worlds, and reset runtime state on reconnect or identity changes. This change supplies normalized state, not new HUD widgets or a manual mapping editor.

## Discovery and publication

1. Read the local schema-2 directory; skip web-only or invalid endpoints. Resolve public addresses and connect to those exact addresses. TLS certificate checks remain enabled.
2. Negotiate GMCP/MSDP with the shared parser. No username, password, character creation or gameplay commands are sent. Probe duration, bytes, events, fields and nesting are bounded.
3. Exclude authentication, secrets, chat and protocol-control fields. Preserve advertised names independently of received scalar types; omit values from schema fingerprints. Object fields have bounded paths; arrays are recorded but not expanded into arbitrary item names.
4. Merge partial observations. A complete MSDP reportable list can retire absent fields from that source. A GMCP partial update cannot. Failed or limited probes preserve prior good evidence and mappings.
5. Apply deterministic known aliases first. Ask Azure about remaining fields in batches of at most 128, persisting batch progress and budget reservations. Model/schema/prompt version changes trigger new work. Unchanged completed schemas do not call Azure.
6. Reject invented paths, unsupported conversions and malformed/truncated outputs. Omit all competing suggestions for a duplicate destination, and omit suggestions that would overwrite an existing binding. The other validated suggestions can proceed. Numeric conversion must be finite; downloaded scripts or expressions are never executed. Maintain last-good publication until a complete new proposal validates.
7. Write `protocol-worker-state.json` and `protocol-mappings.json` atomically. The Python API overlays published maps onto `/directory` responses without modifying provider cache age or artwork.

`protocol-worker-state.json` is operational state, including budgets and resumable proposals. Back it up with the cache. Corrupt or unsupported state fails closed rather than resetting the budget. `protocol-mappings.json` is the inert publication artifact and can be regenerated from state.

Failures retry on the next daily probe; rate limits respect `Retry-After`. Large schemas can span multiple budget days. The mapping contract caps publication at 256 bindings; `mapping_limit` reports a proposal that reaches that bound. Complex collections such as effects and exits are retained as evidence but are not flattened into scalar bindings. Location fields are display data and do not fabricate coherent room-map observations.

## Verification

```sh
dotnet test tests/Wandur.Discovery.Tests/Wandur.Discovery.Tests.csproj -c Release
dotnet test Wandur.sln -c Release
cd directory-server
.venv/bin/python -m unittest discover -s tests -v
```

The test suite uses local TCP and HTTP fixtures. Live crawling and paid mapping calls are explicit operational runs, not part of tests.
