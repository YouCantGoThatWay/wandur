# Wandur

**A doorway to other worlds.** An open-source, text-first MUD client built with C# and Avalonia.

This is the first playable foundation. The client is independent of any particular MUD server. Game connections are always started explicitly. The world editor uses MudConnector’s public directory to suggest names when you enter an address.

## Run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```sh
dotnet restore Wandur.sln
dotnet run --project src/Wandur.Desktop/Wandur.Desktop.csproj
```

Choose **File → Open Offline Demo** to explore a five-room offline world. Try `look`, `north`, `east`, `up`, `who`, `inventory`, and `say hello`. The demo is scripted and runs entirely in the client; it is not the planned MUD server or an AI simulation.

For a real MUD, choose **+ Add world**, paste `host:port` or enter the fields separately, save it, select it in the world library, and connect. UTF-8 is the default; Latin-1 is available for older games. TLS is optional and must be supported on the selected server port; normal certificate validation applies.

## Available now

- Native desktop workspace with draggable, resizable and floating panels, visible drag grips and close buttons.
- Separate **Open sessions** and **Saved worlds** lists in the Workspace sidebar. Each connection has its own ID, transcript, command history, draft, scripts runtime, and private-input state. Connect repeatedly to the same saved world to log in as different characters. Right-click a session → **Rename session**, or press F2; names last for that open session. Background output marks sessions with an activity indicator.
- Dedicated XTerm display with cursor positioning, erase commands, bounded scrollback, selection/copy, 16/256/true colors, bold, italic, underline, and optional blinking text. Built with Avalonia 12.1.2 and Iciclecreek.Avalonia.Terminal 4.0.2.
- Partial-line prompts, carriage-return updates, backspace, selectable/copyable text.
- Bounded scrollback (up to 2,000 lines / approximately 200,000 characters; 4,096 characters per line). Reading older output pauses following; **Latest output** returns to the prompt.
- In-memory command history with up/down navigation and draft restoration.
- Private input with masking and exclusion from local echo/history. Automatic recognition of Telnet server echo and common password prompts; the manual switch covers unrecognized prompts. Sensitive drafts are discarded before unmasking on disconnect.
- Smart address entry (`host:port`, `host port`, `telnet://host:port`, and bracketed IPv6) plus automatic MUD-name suggestions.
- Saved world profiles, four palettes (Ember, Moonlight, Forest, Paper), custom foreground/background colors and text size.
- Explicit plain-text transcript export. No automatic logs or persistent command history. Optional saved login uses the system credential store, never plaintext settings.
- Quick command buttons and a local demo adventure.
- Form-based **Macros** configuration section for text triggers, exact command aliases, repeating timers, and F1–F12 shortcuts. Save rules per world and enable them individually; advanced JavaScript stays in Scripts. See [automation](docs/scripting.md).
- Telnet negotiation for ECHO, SGA, TTYPE, NAWS, GMCP and MSDP. Room metadata feeds the mapper; compact **Play / Diagnostics** tabs show received protocol messages with timestamps and formatted, copyable details. See [protocol diagnostics](docs/protocol-diagnostics.md) for history limits and privacy behavior.

The compact toolbar selects a saved world and connects or disconnects. On macOS it shares the titlebar with the native window buttons; drag its empty space to move the window. Standard menus hold transcript saving, preferences, panels, and session actions. macOS uses the system menu bar, with Preferences under Wandur (displayed as Settings on newer macOS versions); Windows/Linux use an in-window menu bar.

**Settings → Terminal → Allow blinking MUD text** is off by default. Enabling it animates ANSI blinking text independently of command-box focus.

Enter advances the transcript even with local echo disabled. Enable **Show my commands in the transcript** in Preferences to display your submitted commands; private input remains hidden.

Shortcuts: Cmd/Ctrl+T opens Find a MUD, Cmd/Ctrl+N adds a saved world, Cmd/Ctrl+W closes the selected workspace item, Cmd/Ctrl+S saves the current transcript (or all sections when editing a connection), Cmd/Ctrl+D disconnects the active session, and Cmd/Ctrl+L focuses command input. Ctrl+Tab / Ctrl+Shift+Tab switch between search, sessions, and open map editors. Closing the last session returns to search. Disconnected sessions retain their transcripts until closed. Each session remembers its **Play** or **Diagnostics** bottom tab while you switch elsewhere. The footer has separate **Scripts**, **Macros**, and **Agent** controls, with selectable agent goals, Play/Stop, and live status; configure the model and commands in world settings → **Agent settings**. See [agent setup](docs/agent.md).

Drag the shaded panel headers to rearrange them or move them outside the window to float. Use **×** to close a tool panel; **View → Restore Panels** reopens all panels in their default positions while preserving every session. The View menu also toggles Workspace, Controls, and the toolbar individually. Layout arrangement is not yet saved across restarts.

### World-name discovery

The editor downloads the [MudConnector big list](https://www.mudconnect.com/cgi-bin/search.cgi?mode=mobile_biglist) over HTTPS and caches it in memory for 30 minutes. Hostnames are matched locally and are not sent as search queries. Exact host and port matches take priority; a unique name on the same host can also be suggested without changing your port. Ambiguous results leave the name for you to enter. Custom names are never overwritten.

This is a best-effort HTML directory adapter, not a documented API. Listings can be outdated, and directory outages or page changes do not prevent manual naming or saving. The directory is not needed for the offline demo or for connecting to saved worlds.

## Development

```sh
dotnet build Wandur.sln -c Release --no-restore
dotnet test Wandur.sln -c Release --no-restore
```

Tests include fragmented protocol streams, a real loopback TCP server, connection teardown, settings recovery, and rendered Avalonia UI interactions. Headless desktop tests use the actual application styles, views and docking library.

On macOS/Linux, save UI captures while running the tests:

```sh
WANDUR_CAPTURE_DIR="$PWD/artifacts/screenshots" dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj
```

Use an isolated preferences directory for development:

```sh
dotnet run --project src/Wandur.Desktop/Wandur.Desktop.csproj -- --data-dir /tmp/wandur-dev
```

For a local macOS `.app` bundle (requires the .NET 10 runtime):

```sh
bash scripts/package-macos.sh
open artifacts/macos/Wandur.app
```

On macOS external drives, keep the NuGet package cache on the internal disk. The normal `~/.nuget/packages` location works; do not put the cache on an exFAT volume. The project excludes `._*` AppleDouble source sidecars. Use the explicit `.csproj` paths above because metadata sidecars can confuse folder-based project discovery.

## Project layout

| Project | Responsibility |
| --- | --- |
| `src/Wandur.Models` | Game-neutral state and protocol mapping contracts; published as the `Wandur.Models` package |
| `src/Wandur.Protocol` | Telnet negotiation, GMCP and MSDP decoding and protocol diagnostics; published as the `Wandur.Protocol` package |
| `src/Wandur.Core` | ANSI transcript, session lifecycle, mapping, demo, history and settings; no GUI dependency |
| `src/Wandur.Desktop` | Avalonia workspace, docking, input, dialogs and appearance |
| `tests/Wandur.Core.Tests` | Protocol, socket and persistence regression tests |
| `tests/Wandur.Desktop.Tests` | Input, privacy, docking, themes, lifecycle and render checks |

Persistent client data lives in one `wandur.db` SQLite file in the `Wandur` folder beneath `.NET`'s application-data directory. It contains world profiles, scripts, maps, agent settings, directory snapshots/artwork, and historical protocol observations. Password values remain in the operating system credential vault. Existing JSON settings, script libraries, map files, and directory caches are imported transactionally on first use and left in place as migration backups. SQLite uses transactions and indexes for cross-platform lookups; the map graph is still held in memory while a session is active. This version targets Windows, macOS and Linux; native manual testing so far covers Apple Silicon macOS. The CI matrix is configured for all three platforms but has not been run remotely yet.

## Next increments

The [local-model agent](docs/agent.md) supports server address and provider selection for LM Studio native and OpenAI-compatible APIs, automatic model discovery, world instructions and command catalogs, and per-connection Preview/Step/Run controls. The [original proposal](docs/proposals/llm-agent-workspace.md) describes possible extensions.

1. Configurable WHO profiles and a player panel, with conservative output capture.
2. Expand the existing JavaScript scripting API and macro tools.
3. Add more game adapters and mapper package compatibility to the [editable 2D mapper](docs/mapper.md).
4. Optional local entity extraction and cached generated room materials.
5. Detachable session windows and persistent docking layouts.

The dedicated display handles terminal cursor addressing and screen updates; MUD commands still use the separate input box. MCCP compression, MXP, MSP, inline terminal graphics, dynamic terminal resize reporting, and extended encodings are not implemented. Unsupported Telnet options are declined; NAWS currently reports 100 × 40. WHO polling and Python scripting are not implemented yet. A dockable [2D mapping prototype](docs/mapping-prototype.md) supports GMCP/MSDP room metadata, conservative text parsing, and sequence-based location inference.

## Shared packages

`Wandur.Models` (contracts and validation) and `Wandur.Protocol` (telnet parsing and
protocol decoding) are published to NuGet.org by the `Publish packages` workflow when a
`v*` tag is pushed; the tag supplies the version. The repository secret `NUGET_API_KEY`
must hold a NuGet.org key scoped to those two package ids.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Licensed under [MIT](LICENSE). Avalonia, Dock and other dependencies retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## World directory and illustrations

The world directory is served by a separate, private service (the client reads its
base URL from `WANDUR_DIRECTORY_URL`, defaulting to a local instance on port 8765). Choose
**Find a MUD** in the Workspace sidebar, the toolbar magnifier, or **File → Browse Worlds…**.
All entry points open the same center page. It retains the search, filters,
selected listing, and scroll positions while live sessions continue in the background.
Search names, themes, descriptions, or host:port addresses
locally; name searches also tolerate small typos. Filter to MUD connections,
browser-only worlds, or worlds reported online. Selecting a result displays its
supplied banner or cached illustration, full description, and connection details.
Read a listing, add it to your saved worlds, or open a new connection. TLS is
available when the listing provides a TLS port. Browser-only games link to their
websites instead of offering a Telnet connection.

The directory is downloaded on launch when its local snapshot is older than 24 hours.
If the service is unavailable, cached listings and saved worlds continue working.
Pasting a host and port into Add World now looks for an exact match in this catalog.

The client maps listings into its own [versioned Wandur schema](docs/directory-schema.md)
and migrates existing caches automatically. Details include descriptions, connection
and TLS addresses, reported availability and check times, population, language,
theme, roleplay/PvP policies, and website/Discord/browser-play links. Listed player
ranges and last observed counts are labeled separately; missing measurements stay
unknown. No numerical average is inferred from a range.

Expand **Advanced search** to combine theme, game type, language, roleplaying,
player killing, codebase, development stage, world size, location and feature/tag
filters. Optional minimum/maximum **last observed** player counts, minimum rating,
and TLS availability help narrow results. Missing measurements do not match numeric
filters. Clear search & filters resets everything. The sort selector offers best
match, alphabetical, observed population, rating, recent updates and newest worlds.

Results show gameplay categories, population, availability and ratings. Details
include formatted paragraphs/lists, gameplay badges, activity/community facts,
source-specific ratings/review counts/votes and the game's opening year when
supplied. The API's optional `community` and `established_at` fields extend version
2 compatibly; older cached listings work with these details marked unknown.

Supplied MUDVerse banners are displayed first. With Azure configured on the service,
illustrations are automatically generated in the background only for worlds without
supplied artwork (MUDVerse's shared placeholder does not count as artwork). The server keeps artwork indefinitely
and generates a replacement only when its content or image configuration changes.
The desktop displays and caches completed illustrations automatically.

### World editing, saved login, and languages

Right-click a saved world and choose **Edit**, or select it and use the Edit button. Save a username and optionally a password in the system credential store, then enable automatic login. The client uses GMCP password login when the server offers it, or waits for name/password prompts and sends each once. The two paths do not duplicate credentials, and rejected logins are not retried automatically. Custom prompt expressions are available for unusual MUDs. Additional menus or MFA remain manual.

Credential storage supports macOS Keychain, Windows Credential Manager, and Linux Secret Service (`secret-tool` plus an active keyring; Debian/Ubuntu package `libsecret-tools`). Plain Telnet transport is still unencrypted; use TLS where the MUD supports it.

Preferences has a category sidebar: **General**, **Appearance**, **MUD colors**, **Terminal**, and **Input**. Under Appearance, choose a preset and **Create a copy** to save a named custom theme. Edit 18 palette colors with the color picker or `#RRGGBB` values, rename or delete custom themes, and choose light or dark controls. Changes preview live; **Cancel** restores the saved appearance. Turn off **Allow worlds to apply their own themes** to use your palette everywhere. Terminal foreground/background overrides still take priority over palette colors. Custom themes are stored in `wandur.db` alongside the other client settings.

**MUD colors** edits the selected theme’s 16 standard and bright ANSI colors. Copy a preset to customize it, use the color picker or hex input, and reset individual colors to their defaults. Overrides apply to foregrounds and backgrounds, including existing transcript text, even when world-supplied UI themes are enabled. Explicit RGB colors and the extended 256-color cube/grays remain as sent by the server. Each settings category keeps its own scroll position.

Preferences includes English, Spanish, French, German, and Brazilian Portuguese. Language changes preview immediately, including existing menus and controls, without reconnecting sessions. Save keeps the selection; Cancel restores the previous language. Game and directory content is not translated. See [client architecture and localization conventions](docs/client-architecture.md) for DI, MVVM, resource contributions, and platform testing limits.

### JavaScript scripting

Edit a saved world and select **Scripts** for the syntax-highlighted editor. Connection settings use a collapsible section list and a resizable window; **Apply** saves connection fields without closing. The active view’s **Scripts** menu provides connection-local switches and a refresh icon to reload saved rules. **Macros** has its own master toggle. Its compact toolbar provides New, Delete, Save, Enabled, output, and API help. Each world has a library of named scripts that can handle incoming lines, GMCP events, aliases, regex triggers and timers. Enabled scripts resume with future connections; each runs in an isolated worker. See [the scripting guide](docs/scripting.md) for examples, limits and supported APIs.

### Editable 2D mapper

The [mapper guide](docs/mapper.md) covers colored room graphs, contiguous grid areas, floors, terrain and symbols, room/exit editing with undo, search, weighted routes, verified walking and JSON import/export. The Map panel reports negotiated GMCP/MSDP support separately from structured room fields received. The optional local room classifier that colors rooms by inferred terrain is described in [the room terrain inference design](docs/superpowers/specs/2026-09-18-room-terrain-inference-design.md) and its original [proposal](docs/proposals/room-environment-classifier.md).

## Moving the checkout

All build scripts and project references use relative paths; the containing folder
can be renamed or moved. Reopen `Wandur.sln` and run `dotnet restore Wandur.sln`
after moving. The client uses its own application-data folder, independent of
the checkout. Release builds omit debug symbols to avoid embedding developer checkout paths;
Debug builds retain symbols for development. The macOS bundle identifier is `net.wandur.client`.
