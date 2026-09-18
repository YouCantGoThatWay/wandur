# Claude handoff: live resource bars and directory mapping refresh

Updated September 18, 2026. Work paused at the user's request because Codex credits were running low. **Finish the current refresh/Icesus changes, verify, and rebuild. Do not restart the user's MUD sessions without being asked.**

## User intent and latest clarification

Wandur is a C#/.NET 10 Avalonia MUD client with a Python directory API and a new C# discovery worker. The user wanted live progress bars for available character resources, then noticed no bars in Icesus and asked whether existing saved worlds automatically receive updated server mappings.

- **Jedi is not a confirmed bug.** The user ultimately said: “Oh I just hadn't logged in far enough.” Its mapping already existed; actual character values arrive after login. Do not keep debugging Jedi as a broken mapping.
- Icesus really lacked a mapping. Its anonymous probe returned **MSSP server metadata**, not GMCP character fields. These are different protocols/data sets.
- Existing clients also really lacked periodic directory refresh and adoption of new mappings in active connections. Fixing that is in progress.
- The user asked about Icesus's Mudlet implementation. It is an **extension/package for Mudlet**, not a fork. We used its maintained public GMCP definitions to supply our own declarative mapping. We do not execute its Lua.
- User dislikes repeated approval questions, wants work completed autonomously within scope, and prefers plain language without em dashes.

## Workspace and operating notes

- Root: `/Volumes/Extreme SSD/workspace/Wundur` on macOS. **Not a Git checkout.** No AGENTS.md found previously.
- .NET 10, Avalonia 12.1.2. Core/worker xUnit 2; desktop xUnit 3 + Avalonia Headless.
- Use exact `.csproj` paths or `Wandur.sln`; AppleDouble `._*` files exist.
- Tests/builds often need escalated execution because sandbox blocks .NET build pipes and loopback sockets.
- Backend venv: `directory-server/.venv/bin/python`.
- Never print `.env` or credentials. Azure configuration is already working.
- Desktop DB: `~/Library/Application Support/Wandur/wandur.db`. Do not dump profile payloads; they may contain private settings.

## Already completed and shipped before this fix

1. `src/Wandur.Models`: shared game-neutral state, mapping contracts and validation.
2. `src/Wandur.Discovery.Worker`: anonymous GMCP/MSDP discovery, schema fingerprints, deterministic aliases plus Azure mappings, bounded budgets, last-good retention, daily operation.
3. Python `/directory` overlays validated worker mappings. Client caches/consumes them through `ProtocolBindingEngine`.
4. `ResourceBarsView` above the command composer, showing finite current/max resource and progression pairs. Hides missing maxima, unpaired levels, disconnected/empty state. Clamps fill but retains actual numeric text. Responsive columns and bounded scrolling.
5. **Last full green baseline: 681 tests** = 390 Core + 266 Desktop + 25 worker. Log `/tmp/wandur-resource-full-tests.log`. App bundle was rebuilt then at `artifacts/macos/Wandur.app`. That bundle predates the current refresh fixes.

Worker deployment: `net.wandur.discovery` user LaunchAgent, installed by `scripts/install-discovery-worker-macos.py`, daily interval and `--once`. Reads `directory-server/.env`; model `gpt-5.6-luna`, mapping daily cap now **20 calls**, request interval 5 seconds. Initial sweep used 59 worker requests, plus two diagnostics, about $0.0714 at published rates. 48 generated/fallback maps with 1,044 bindings before Icesus curation. Three proposals retained deterministic fallbacks after invalid model output. Model mappings remain provisional.

## Current root-agent changes: implemented and targeted checks green

### Icesus curated mapping

- New `directory-server/mappings/curated.json` and `mappings/README.md`.
- `protocol_mappings.overlay` accepts optional curated catalog; valid reviewed maps override worker maps after the same endpoint/schema validation. Bad overrides leave valid worker maps intact. Worker cannot overwrite the curated file.
- `directory-server/app.py` uses that curated catalog on `/directory` responses.
- Exact scope: world `mudverse:645`, `play.icesus.org:4000`, plaintext.
- Four resource pairs:

| Resource | Current | Maximum |
| --- | --- | --- |
| Health | `Char.Vitals.hp` | `Char.Maxstats.maxhp` |
| Spell points | `Char.Vitals.mana` | `Char.Maxstats.maxmana` |
| Endurance | `Char.Vitals.moves` | `Char.Maxstats.maxmoves` |
| Psionic points | `Char.Vitals.psp` | `Char.Maxstats.maxpsp` |

- `Char.Base.name` supplies identity/reset. Nine bindings total. Zero psionic maximum produces no bar.
- EXP intentionally omitted: official client changes denominator from `tnl` to `tna` at level 100; our mapping contract lacks conditional transforms.
- `ProtocolDiscovery.GmcpSupports` explicitly includes `Char.Base`, `Char.Vitals`, `Char.Maxstats`, `Char.Status` alongside existing modules.
- `ResourceBarsView` recognizes spell_points/endurance/psionic_points for order/colors.
- `IcesusResourceMappingTests.cs` loads actual curated JSON, applies separate packets, verifies three bars and character-reset behavior. Desktop csproj copies the curated JSON as a fixture.
- **44 Python backend tests pass**: `/tmp/wandur-icesus-mapping-backend.log`.
- **34 RoomProtocol tests pass**: `/tmp/wandur-icesus-support-test.log`.
- **Icesus packet/UI test passes**: `/tmp/wandur-icesus-pairs-test.log`.
- Screenshot inspected: `artifacts/screenshots/resource-bars/icesus-resource-bars.png`.
- Independent reviewer found no actionable issues in backend, curated map, subscriptions or resource display changes. It has **not reviewed the newer refresh-agent changes**.

Sources:
- https://github.com/Icesus-mud/mudlet-package/blob/master/package/Icesus.xml
- Official source downloaded read-only to `/tmp/icesus-official-package.xml`. `onVitals`, `onMaxstats`, `onBase`, `subscribeGMCP` are relevant. Comments around 4405 say re-sending `Core.Supports.Set` prompts a fresh package dump.
- https://icesus.org/guides/clients/

### Local publication already updated

- Restarted only the local directory API, port 8765. Log `/tmp/wandur-directory-icesus-mapping.log`.
- Verified `/directory` publishes Icesus's nine bindings and preserves `icesus-frozen-night-v1` theme.
- Backed up DB and overlaid mappings into cached catalog: **49 worlds** now mapped. Latest backup `/tmp/wandur-before-protocol-mappings-20260918T154933Z.db`.
- Helper `/tmp/wandur-update-catalog-mappings.py` updates only catalog mappings, preserving other fields. Existing profiles are intended to update through the new client refresh logic.
- Before that cache update, saved Icesus profile had no map; saved Jedi profile already had 67 bindings.

## Refresh agent changes: implemented, verification incomplete

Agent `live_catalog_refresh` was interrupted for this handoff. Its files are shared and already present. No cherry-picking needed.

Changes:
- `src/Wandur.Core/Discovery/WorldCatalog.cs`: five-minute local attempt throttle independent of upstream `FetchedAt`. Fetch on first startup even when provider snapshot is younger than 24 hours. TimeProvider supports tests.
- `src/Wandur.Desktop/MainWindow.cs`: periodic refresh timer, start/stop with window lifetime, avoid overlap.
- `src/Wandur.Desktop/SessionWorkspace.Appearance.cs`: refresh saved profiles in place and active tabs from validated endpoint-bound mappings. Preserve user settings and last-good maps when optional data absent/bad. No re-add needed.
- `src/Wandur.Core/Protocol/ProtocolBindingEngine.cs`: `UpdateMapping` preserves state only for compatible additive changes with unchanged identity rules. Otherwise reset and wait for fresh public data. Never replay diagnostic history.
- `WorkspaceController.Diagnostics.cs`, `WorkspaceController.cs` and `WorkspaceController.Mapping.cs`: adopt updated maps, queue subscription refresh, defer during private/login handling.
- `src/Wandur.Core/Sessions/TelnetSession.cs`: `RefreshProtocolSubscriptionsAsync`, fixed GMCP support advertisement and bounded native/tunneled MSDP `REPORT` + `SEND` for validated mapped names. Negotiated protocols only, send lock and privacy epoch checks.
- Tests in `WorldCatalogTests.cs`, `ProtocolBindingTests.cs`, `ProtocolMappingSessionTests.cs`, `SessionTests.cs` (confirm exact diff by reading; no Git).

**Known immediate test failure to fix:**

`tests/Wandur.Core.Tests/SessionTests.cs`, new `NativeMsdpRefreshReportsAndRequestsMappedVariablesWithoutNegotiatingGmcp`, approximately lines 70–71: its two `FieldBinding` fixtures lack required nonempty `Label`. Add `Label = "Health"` to both. The mapping is currently rejected before refresh behavior is exercised.

Agent's latest checks:
- Desktop `ProtocolMappingSessionTests`: **5/5 pass** (tool session 3438 completed).
- Earlier catalog/binding checks: **49/49 pass**.
- Latest Core filtered suite: **50/51 pass**, sole failure is the missing-label fixture above (session 71903 completed).
- No intentional active test runs remain. A sandbox-failed MSBuild session 39463 may linger.
- No dedicated desktop MSDP privacy-transition test yet. Servers can ignore resubscription requests, so newly mapped values still depend on fresh packets actually arriving.

## Remaining work

1. Fix that test fixture; rerun affected Core/Desktop tests. Review protocol resubscription lifecycle, privacy transitions and unchanged-map deduplication.
2. Run full Release solution tests and localization check. Previous 681 baseline is not evidence that all current changes pass.
3. Run backend suite if backend changes further; current 44 passed.
4. Update `docs/verification.md` with Icesus/automatic-refresh results. Existing doc already covers earlier resource bars/discovery/MSSP work.
5. Rebuild `artifacts/macos/Wandur.app`. Consider republishing the installed discovery worker too, since shared protocol support advertisement changed; installer will run once and respect existing budget/timestamps.
6. Verify `/directory` and client cached mapping still have Icesus. No need to re-add or alter login credentials.
7. Tell user one app restart is needed to load the new executable; after that directory data refreshes periodically and mappings can update active sessions. Do not claim a live Icesus login was tested: only official definitions + simulated protocol packets were verified.

Useful commands from root:

```sh
dotnet test Wandur.sln -c Release --no-restore
python3 scripts/generate-localization.py --check
bash scripts/package-macos.sh
```

Backend from `directory-server`:

```sh
.venv/bin/python -m unittest discover -s tests -v
```

## UI inspection caveat

Computer-use inspection used `@oai/sky` through node_repl. It returned a disconnected Wandur window with zero sessions, so no live Jedi payload was inspected. The tool may have opened the app if it was not running. User subsequently confirmed Jedi only needed further login. Do not infer a protocol bug from that earlier report.

The previous MSSP survey is separate: `artifacts/discovery/mssp-survey.md` and raw JSON. 115 listings returned MSSP, including Icesus. MSSP is not yet wired into the daily worker and does not supply player vitals mappings.
