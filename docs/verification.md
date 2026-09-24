# Client foundation verification

## Local session history (2026-09-24)

- Implemented on main: View > Session history, literal FTS5 keyword/phrase search,
  world/character/date filters, paginated context, confirmed deletion and saved
  recording/retention preferences. Default retention is 30 days. No embeddings,
  new packages or services. See [session history](session-history.md).
- Final Release solution tests passed: 771 Core and 574 Desktop, 1,345 total,
  zero failures or skips. Release build: zero warnings and errors. Localization
  facade and whitespace checks passed.
- Regression tests use temporary SQLite databases, fictional text and a real
  loopback TCP fixture, not user data or public MUD connections. Coverage includes
  migration/reopen, FTS consistency, active-session deletion races, abandoned
  session retention, stream fragments, private/login capture, cancellation,
  queue overflow, storage failure and orderly shutdown.
- Independent review found four issues: credential fragments across command
  boundaries, suffixes following private received text, ANSI edits shrinking an
  over-limit line and retention of abandoned session metadata. All four were
  reproduced RED and fixed GREEN; focused post-review tests passed 58/58.
- Rendered the populated history browser in all five languages and a Paper
  deletion confirmation. Slate history and Paper confirmation were visually
  inspected; captures are under `/private/tmp/wandur-history-ui-captures/`.
- No running client was restarted, no existing profile/history database was
  inspected and the macOS app bundle was not replaced. Native Windows UI and
  real-server login are not manually verified. Storage is plain text, not
  encrypted; private-boundary handling may omit one public line after login.

## Initial foundation verification

Verified on Apple Silicon macOS, using .NET SDK 10.0.101.

- Release tests: 35 passed (28 core, 7 desktop), zero failures.
- Release solution build: zero warnings and zero errors. Local macOS app bundle rebuilt and launched after the final fix; native screenshot confirmed the complete prompt is visible.
- Desktop tests run the real Avalonia styles and controls with Skia rendering; screenshots are written to `artifacts/screenshots` when `WANDUR_CAPTURE_DIR` is supplied.
- The socket tests use an actual loopback TCP listener and verify UTF-8 fragmentation, CRLF commands, disconnect detection and socket cleanup.
- UI tests cover typed commands, private input/history exclusion, sensitive draft clearing on disconnect, real floating panels, layout reset without losing the session, saved theme/canceled preview, refused connections, concurrent teardown/start, and the final prompt's actual position inside the viewport.
- Native macOS app launched; offline demo opened and `north` entered through the native command field. The market appeared correctly.
- Independent code review identified sensitive-draft exposure, local-echo interference with fragmented ANSI, and overlapping lifecycle operations. All three were fixed with regression coverage.
- Native inspection identified clipped final prompts caused by scroll-container padding. Padding moved to content margin and an actual geometry assertion was added.

The local macOS bundle is framework-dependent: it requires .NET 10, and is built for the architecture of the machine running the packaging script. It is a development build, not a signed/notarized public release.

Not exercised here: public MUD login, real TLS servers, native Windows/Linux windows, and remote CI. Cross-platform build/test CI is configured but has not run remotely. No external MUD received traffic during development.

## Visual refresh · September 15, 2026

- Reworked the shell, docking chrome, typography, welcome screen, world library and command panel. Buttons use vertical gradients, highlight edges, shadows, hover feedback and an inset pressed state. All four palettes share these treatments.
- Final Release verification: 35 tests passed (28 core, 7 desktop). Rebuilt the macOS bundle and opened the offline demo in the native app.
- Inspected rendered session and welcome captures, native dark preferences, Paper preview, and restoration of Ember on cancel. Captures are in `artifacts/screenshots`; `modern-client.png` shows the native app.
- Preserved keyboard input, private input handling, command history, floating panels and layout reset. Command buttons with composed labels now expose explicit accessibility names.
- Template selectors checked against the pinned [Avalonia TextBox source](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Themes.Fluent/Controls/TextBox.xaml) and [Button source](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Themes.Fluent/Controls/Button.xaml).

## Docking and world discovery · September 15, 2026

- Tool panels now have shaded headers, dotted grips, drag cursors/tooltips and visible close buttons. Restore panels reopens them in the default arrangement without stopping the session.
- Smart address entry separates host/port on paste and normalizes manually typed addresses on focus loss or save. Supported forms include host:port, host port, telnet URLs and bracketed IPv6.
- World-name discovery fetches the user-supplied MudConnector mobile big list over HTTPS, then matches locally against a 30-minute memory cache. It prefers exact host/port, accepts a unique same-host name, and leaves ambiguous names alone. Lookups are debounced, cancellable, bounded and cannot replace a custom name. Failure leaves manual saving available.
- Release suite: 53 passed (42 core, 11 desktop). New tests cover address parsing/rejection, name matching/cache reuse, paste/save, late results versus typed names, offline saving, and closing/restoring both panels with the session alive.
- Native packaged app: pasted aardmud.org:23, confirmed separate host and port, and confirmed Aardwolf autofill from the live directory. The first native request failed transiently; a retry and a separate .NET request succeeded. The failure state was nonblocking.
- Native Controls close button removed the panel; Restore panels brought it back while the offline demo remained active. Screenshots: world-discovery.png and dockable-panels.png in artifacts/screenshots.

## Second-world save fix · September 15, 2026

Reproduced saving Zee MUD followed by d20MUD in isolated settings. The second save wrote settings successfully but then threw NullReferenceException when WorldLibraryView's item template received null while Avalonia recycled the original row. The template now returns no content during that transient empty state.

The regression verifies both saves close their dialogs and retain both profiles in the visible library. Final Release tests: 54 passed (42 core, 12 desktop). Rebuilt the macOS app bundle. No user profiles were edited for verification.

## Connection error diagnosis · September 15, 2026

The saved mud.d20mud.com hostname failed DNS resolution. The official d20MUD Network homepage lists starwars.d20mud.com:5500; DNS resolved it to 74.208.126.44 and a short unauthenticated connection returned the d20MUD Star Wars banner. Corrected only the affected saved profile hostname. No account was created or logged in. DNS failures now identify the hostname and explain that a directory entry may be outdated. Final Release suite: 57 passed (45 core, 12 desktop); app bundle rebuilt.

## Desktop menus and session tabs · September 15, 2026

- Replaced the large brand header with a compact raised toolbar and connection status. File, Edit, View, Session, Window and Help menus expose implemented actions and platform shortcuts. macOS uses NativeMenu; other platforms use an in-window Menu backed by the same commands. Preferences belongs to the application menu on macOS, where current macOS displays it as Settings.
- Each session tab owns a WorkspaceController, transcript, command history, private state and retained TerminalView/draft. Background network output marks unread tabs. Shared saved settings propagate across tabs. Closing a tab disposes only its connection; closing the last tab creates a blank replacement. Tool panels follow the selected session, including when floating.
- Regression first reproduced the single-session restriction. Final Release suite: 62 passed (45 core, 17 desktop), no failures. Tests exercise two real loopback connections, background activity, socket cleanup, retained drafts/private masking, active menu routing, shared settings and panel/toolbar visibility, plus all previous regressions.
- Native macOS QA: application Settings menu opens the existing preferences dialog; File → Open Offline Demo opens a second connected tab; entered north in the second session, switched with Ctrl+Tab, then closed the first with Cmd+W, leaving the second connected. Inspected compact toolbar and session tabs visually. Application menu initialization moved into App.axaml after native QA caught replacement of the already-initialized default application menu.
- Rebuilt the framework-dependent macOS bundle. Windows/Linux native rendering has not been manually exercised. Session tabs do not yet detach into separate windows, and docking layout/session restoration across restarts remains future work.

### Tab polish and Enter behavior

- Removed the Fluent blue selected-tab indicator. Selected tabs use the terminal background, and close controls are a plain zero-padding, borderless × that becomes bold on hover.
- Reproduced the reported missing newline with an actual loopback MUD: with echo disabled, a prompt and response became `Room> A quiet room.`. Submitting a command now advances the local transcript before waiting for the network write, so even an immediate response follows the prompt on a new line. Echo preferences still control command text; private input is never echoed.
- Final Release suite: 64 passed (45 core, 19 desktop), including both normal and private no-echo newline regressions. Existing user connections were left running; the rebuilt package requires a restart to load these latest fixes.

## Local directory service and automatic Azure artwork (2026-09-15)

- Added `directory-server/`: FastAPI, MUDVerse pagination and full details, a single
  JSON file with 24-hour expiry, atomic refresh, retained stale data on failure,
  rate pacing, and shared refresh requests.
- Azure image generation runs automatically in a background worker after import.
  PNG files persist without expiry; hashes invalidate artwork when the description
  or image configuration changes. No generation button or paid client endpoint.
- Desktop Browse Worlds has local fuzzy search, full descriptions, Add/Connect,
  TLS selection, safe website links, automatic artwork display, and offline caches.
- Automated verification: **75 tests passed** (48 core, 20 desktop, 7 Python).
  New coverage includes TTL/restart behavior, corruption, failed refresh retention,
  pagination, concurrent refresh/generation, automatic artwork reuse and content
  invalidation, local search ranking, exact endpoint lookup, duplicate prevention,
  and browser-only connection handling. Upstream API responses were mocked.
- Release build and macOS package succeeded. Headless populated-browser screenshot:
  `artifacts/screenshots/directory-browser.png`. Native isolated QA app confirmed
  Browse Worlds opens and shows a useful unavailable-service state.
- Real MUDVerse and Azure credentials were not used for verification. Existing user
  MUD sessions were left running; the rebuilt app takes effect on the next launch.

### MUDVerse collection timeout fix

- Reproduced authenticated `ReadTimeout` for page sizes 2, 10, and 50; page size 1
  and individual detail requests returned HTTP 200. Curl reproduced the collection
  timeout, confirming it was not specific to HTTPX. The configured key was valid.
- Importer now falls back to single-listing pages after a collection read timeout,
  preserving stable order and resuming after already collected records. Health
  includes `listing_page_size`. Errors distinguish timeouts, authentication, network,
  malformed responses, and disk writes without exposing secrets.
- All **10 Python tests passed**, including the new regression cases. Live check
  observed fallback from 50 to 1, total 240, and three complete records downloaded.
  The bounded live check used a temporary cache and disabled Azure generation.
- The user's running Uvicorn process needs a restart to load the updated source.

### Supplied artwork precedence

- The cached directory contains 144 distinct custom banners and 96 entries using
  MUDVerse's shared placeholder. Supplied custom banners now bypass generation
  and take priority over previously generated artwork. The placeholder remains
  eligible for Azure generation.
- The art endpoint redirects to supplied banners. The desktop reads their URLs,
  caches them separately from generated art, preserves their aspect ratio, and
  identifies their source. Supplied art works without Azure configuration.
- **83 tests passed** (50 core, 20 desktop, 13 Python), covering source precedence,
  generation suppression, placeholder fallback, client cache invalidation, and
  offline reuse. macOS package rebuilt; existing user MUD sessions left running.
- Native QA confirmed Aardwolf's supplied JPEG banner loads in the world browser,
  fits without cropping, and displays the supplied-artwork caption.

### Wandur directory schema

- Client catalog data now uses the version-2 `wandur.directory` schema, with
  namespaced identities and a dedicated MUDVerse mapper. Old caches migrate
  atomically while keeping their original fetch time; native Wandur snapshots
  also load without requiring MUDVerse fields.
- **76 C# tests passed** (56 core, 20 desktop), including normalized offline
  reload, legacy migration, metadata mapping, null versus zero population,
  listed ranges versus averages, unsupported/duplicate snapshot retention,
  supplied-art cache reuse, generated-art routing, and browser add/search.
- Built the macOS bundle and launched only the isolated QA app. Its real
  240-world legacy snapshot migrated successfully. Verified Aardwolf's banner,
  cleaned description, population range, labeled gameplay facts, source dates,
  and links. Screenshot: `artifacts/screenshots/directory-world-details.png`.
- No server changes or server restart required for this change. The rebuilt
  desktop takes effect on the user's next launch; their active sessions were
  not restarted.

### Server schema regeneration and Azure endpoint normalization

- Server import, disk cache, `/directory`, and artwork worker now use the Wandur
  version-2 schema. Migrated all 240 cached listings without a provider download;
  retained the original as `directory-server/cache/directory.mudverse-v1.json`
  and preserved cache age. All 240 mapped records match the desktop's normalized
  snapshot exactly.
- Azure configuration contained a Foundry `/models` URL. Common resource-root,
  `/models`, `/openai/v1`, and full v1 Images URL forms now normalize to the same
  HTTPS resource root without changing the host, key, or deployment. Invalid
  project/credential URLs still fail with sanitized errors.
- **97 tests passed** (20 Python, 57 core, 20 desktop). Added a shared Python/C#
  contract fixture, version-1 cache/art migration checks, and endpoint-form checks.
- Restarted the local directory service on port 8765. Live `/directory` returned
  HTTP 200, `format: wandur.directory`, schema version 2, and 240 worlds. Azure's
  read-only models endpoint returned 200; the worker subsequently processed six
  worlds without an error. Verified a generated image at
  `/worlds/mudverse:452/art` returned HTTP 200 and a valid PNG signature (2,514,903
  bytes); Aardwolf's supplied art still returned a 307 redirect to MUDVerse.
- The existing Azure `.env` settings work as written. No key or deployment change
  was required; desktop sessions were not restarted.

### API-backed MUD server search

- Added the Find a MUD toolbar entry, connection-type and reported-online filters,
  result counts, and host/port search. Artwork preserves its aspect ratio, with
  compact supplied banners and a placeholder while generated art is unavailable.
- **78 C# tests passed** (57 core, 21 desktop). The new integration test exercises
  native API snapshots, supplied and generated image decoding, connection/status
  filters, TLS-only world creation, browser-only restrictions, and offline reuse.
- Rebuilt the macOS bundle and verified the isolated native QA app against the
  running local API: refreshed 240 worlds, displayed A Tempest Season's generated
  illustration and Aardwolf's supplied banner, found Aardwolf using its host/port,
  and filtered to 18 browser-only worlds with native add/connect disabled.
  Screenshot: `artifacts/screenshots/mud-server-search.png`.
- The server currently reports an Azure HTTP 429 for additional image generation;
  existing images and directory browsing remain usable. Closed only the isolated
  QA app. The user's desktop sessions remain running; the new build takes effect
  on their next launch.

### Advanced search and richer directory details — September 16, 2026

- Added an expandable set of combinable gameplay/category/tag filters, minimum
  and maximum observed population, minimum rating, and TLS availability. Text,
  connection and online filters combine with these preferences. Sorting supports
  relevance, name, population, rating, update date and game creation date. Reset
  clears the entire search; an inverted population range has a visible explanation.
- Results now show categories, observed population, availability and ratings.
  Details have gameplay badges, population/rating cards, formatted plain-text
  paragraphs and lists, activity/community facts and gameplay/connection facts.
- Added optional `community` and `established_at` fields to both importers and the
  shared version-2 contract fixture. Enriched the existing 240-world server cache
  from its raw backup, preserving its original age and artwork identities. Saved
  the prior normalized file as `directory.before-community.json`. Restarted the
  local API to use the new importer on subsequent refreshes.
- **100 tests passed**: 57 core, 22 desktop, 21 Python. The new desktop integration
  test covers combined preferences, text intersection, reset, zero versus unknown
  population, inverted bounds, minimum ratings, TLS and population ordering.
  Python tests verify metadata normalization and invalid/unknown measurements.
- Native QA confirmed the expanded controls fit, and a live refresh displays
  ratings and opening dates. Automated screenshot: `artifacts/screenshots/directory-advanced.png`.
  Built `artifacts/macos/Wandur.app`. The user began using the QA window for a
  Merentha connection during verification, so further UI interaction stopped;
  that connected session and the user's other running app instances were retained.

### Compact workspace — September 17, 2026

- Whole-app theme correction: world selection now updates application resources, Fluent controls, dock chrome, titlebar/toolbars, dialogs, map canvas and script editor. Session-tab changes select that session's appearance; unthemed worlds restore the saved preference. Removed the earlier per-panel scopes. Dialog preview restoration, same-row clicks, changed catalog responses, live status updates and Preferences cancel are covered by integration checks. All **143 Desktop tests passed** and localization generation check passed; full-workspace capture inspected at `artifacts/screenshots/world-themes/whole-workspace.png`.

- World search follow-up: listing details now order a larger title, artwork, compact tags/population, description, then connection actions and additional facts.
- Added optional versioned world palettes to the REST schema and SQLite-backed profiles. The Legends of the Jedi prototype uses navy/graphite, cyan and gold; themes are scoped to listing details and session content. Tests cover malformed optional data, endpoint matching, theme isolation, theme removal, explicit terminal overrides, profile edits and credential preservation.
- Validation: full suites passed with **255 Core, 139 Desktop and 24 server tests**. After review identified an optional-cache failure that could block connecting, added a read-only-store regression and reran the affected theme/localization suites (**23 Core and 8 Desktop checks**, plus all 24 server tests). Localization generation check passed, scoped directory screenshot inspected, and the macOS app rebuilt. Existing app connections were retained.
- Server cache enrichment was verified offline: 240 listings retained, only the LOTJ theme added, original `fetched_at` and nanosecond mtime retained, no upstream/image-generation calls. The running service needs a restart for future import enrichment; the existing cache already contains the theme.

- Follow-up: map editing now opens as its own dockable document, with a separate canvas and resizable inspector. The document can float into a window, stays tied to its original session, and shares map edits with the compact live panel. Editor fields and import/export no longer occupy the live panel. Reopening selects the existing editor; close/reset/reconnect cleanup leaves connections and committed edits intact.
- Follow-up validation: **383 tests passed** (247 Core, 136 Desktop), including editor opening/reuse, shared updates, floating, session isolation, reconnect cleanup and layout reset. Localization check passed; editor screenshots inspected and macOS bundle rebuilt.

- Extended the macOS client into the native titlebar; toolbar controls share its row with traffic lights. Empty space supports dragging and hiding the toolbar retains a usable titlebar. Other platforms retain their existing window decorations.
- Reused one browser control in dialogs and empty session tabs. Compact result rows and headings prioritize descriptions. Tests cover independent tab searches, detach/reattach, first empty/populated rendering, and browser-to-terminal transitions.
- Map tools now open in an overlay from the compact toolbar. The map keeps the rest of the panel. Saved-map gaps were disconnected room positions; grid mode preserves those coordinates. A regression protects visible drag distance, hit testing and undo.
- **379 tests passed** (247 Core, 132 Desktop); localization generation check passed. Browser captures inspected at 1380 and 1040 widths, plus grid and editor captures. Native macOS preview verified titlebar integration, dragging and the directory toolbar button using isolated data. Rebuilt `artifacts/macos/Wandur.app`.

### Legacy ANSI bright colors — September 16, 2026

- Inspected the user's Legends of the Jedi transcript: bold near-black text on
  the dark terminal was unreadable. A separate passive connection to the public
  greeting confirmed repeated `ESC[1;30m` sequences (along with bright green,
  yellow, cyan, white and indexed color). No account/login commands were sent.
- The parser applied bold font weight without the legacy bright-palette mapping.
  It now resolves basic foreground colors 30–37 to palette entries 8–15 while
  bold is active. SGR 22 restores normal intensity; defaults/reset and explicit
  bright/indexed/true-color selections clear or preserve the correct state.
  Background colors remain independent of bold. Blink remains ignored and was
  not present in the captured greeting; inverse text is still unsupported.
- **85 C# tests passed** (62 core, 23 desktop). Regression tests first reproduced
  the near-black result, then verified parameter ordering, fragmented input,
  reset behavior, explicit colors and the actual Avalonia foreground/background
  brushes. Rendered output inspected in `artifacts/screenshots/ansi-bright-colors.png`.
- Rebuilt the macOS app. Existing user connections remain running; they need a
  relaunch/reconnect to use the updated parser.

### Optional theme textures — September 17, 2026

- Added versioned image URLs with local artwork-cache reuse (SQLite in the app),
  bounded PNG downloads and cancellation when changing themes. Optional asset
  failures retain the palette. Existing theme-only snapshots remain compatible.
- LOTJ retains its navy panels, cyan/gold accents and solid transcript. Brushed
  gunmetal is confined to titlebars, toolbars and header resources following the
  user's clarification; panels and ordinary buttons receive no metal texture.
- Passed **258 Core, 144 Desktop and 26 server tests**, plus the localization
  generation check. Coverage includes offline image reuse, invalid/oversized
  assets, static serving without upstream requests, restoring unthemed appearance
  and cancellation before a queued image request starts. Rendered capture inspected
  at `artifacts/screenshots/world-themes/metallic-workspace.png`.
- Enriched the server cache locally, preserving listing data and refresh age.
  Restart the directory service to enable the static asset route; no running
  client connection or server process was stopped.

- Review caught possible upscaling of narrow textures; decoding now caps width at
  the original width or 1024, whichever is smaller. All six affected Desktop
  checks passed afterward, including a new small-image regression. Rebuilt the
  macOS bundle after that correction.

### Wandur naming and packaging — September 17, 2026

- Renamed solution, project directories/files, namespaces, assembly names, XAML
  resources, localized labels, protocol client identification, Python package,
  directory schema/header/environment keys, fixtures, CI and documentation.
  The workspace's containing directory remains unchanged for the owner's move.
- Product data uses `Wandur/wandur.db`; credentials use the new platform-specific
  app identity. Existing local client data is retained separately; public builds
  start with the new identity and contain no former-brand compatibility code.
- Updated the local server snapshot offline while preserving its timestamp and
  copied 34 cached generated illustrations to their new cache identities.
- Passed **258 Core, 145 Desktop and 26 server tests**. NuGet locked restore,
  uv offline lock check, localization generation and package-script syntax checks
  passed. The renamed macOS bundle is `artifacts/macos/Wandur.app`, with identifier
  `net.wandur.client`. Release builds omit debug symbols because the Avalonia XAML
  compiler embeds absolute source paths independently of compiler path mapping.
  Both first-party assemblies passed checks for personal paths and prior branding.
- Source references were reviewed independently. No GitHub account, repository,
  author identity or signing certificate was created or changed. No running client
  connection was closed. Moving the checkout requires recreating the Python venv;
  README includes the commands.

### Header texture and palette correction — September 17, 2026

- Confirmed the running local service serves the PNG asset successfully. It is
  still an older service process and must be restarted to adopt current code.
- Reproduced dock headers bypassing the material: all three used the button
  gradient. They now use a dedicated header resource, preserving the ordinary
  button finish. Regression checks fail before and pass after the correction.
- Chrome composition crops a shallow strip of the square material instead of
  compressing all of its grain into toolbar height. Revised LOTJ palette v2 uses
  charcoal, slate and muted blue-gray; metal remains confined to bars/headers.
- Passed 145 Desktop and 26 server tests; inspected the rendered image at
  `artifacts/screenshots/world-themes/slate-metallic-workspace.png` and rebuilt
  `artifacts/macos/Wandur.app`. Server cache palette enriched offline without
  changing listing data, refresh age or artwork. Existing sessions retained.

### LOTJ graphite palette — September 17, 2026

- Refined the non-metal surfaces to graphite, warm silver text and amber accents.
  Saved-world selections use a light accent tint; ordinary buttons and panels
  remain plain. Kept the metal material and its existing scope. Transcript imagery
  remains a future option, with no background image added behind text.
- Passed nine affected Desktop theme/library checks and all 26 server tests;
  inspected `artifacts/screenshots/world-themes/graphite-amber-workspace.png`.
  Rebuilt the macOS bundle and restarted the local directory service. Verified
  its live response includes all 240 worlds and LOTJ palette v3. Cached metadata,
  artwork and refresh age were preserved; no MUD connection was restarted.


## Custom palettes and sectioned settings · September 17, 2026

- Settings now uses a persistent category sidebar: General, Appearance, Terminal and Input. Appearance includes preset copying, named custom palettes, renaming/deletion, light/dark control appearance, 18 color pickers with hex inputs, live previews and an explicit world-theme override preference. All new labels and validation messages are localized in five languages.
- Personal palettes persist in the existing SQLite settings payload. Draft dictionaries are copied so editing or deleting a palette cannot mutate saved settings before Save. Cancel restores the previous appearance; saving preserves world profiles added while preferences were open.
- Full Release suite: 410 passed (260 core, 150 desktop), zero failures. Tests cover SQLite round trips, malformed theme recovery, invalid draft rejection, profile preservation, cancel/deletion isolation, world-theme precedence, preset map/editor color fidelity and sidebar/color bindings. Localization facade check passes.
- Inspected the rendered settings capture at `artifacts/screenshots/settings/custom-theme-editor.png`. Rebuilt `artifacts/macos/Wandur.app`; the running client was not restarted. Native Windows/Linux interaction was not exercised.


## Live language, settings navigation and ANSI palettes · September 17, 2026

- Each settings section has its own scroll viewer; switching from a scrolled Appearance list to a short section keeps its controls visible. MUD colors has a dedicated sidebar entry and theme selector.
- Added defaults, per-theme overrides and individual resets for all 16 standard/bright ANSI colors. The parser preserves palette indices through fragmented, bold and extended sequences; the view uses live resource bindings for indexed foregrounds/backgrounds. Explicit RGB and indices above 15 are unchanged.
- Language selection previews existing labels, menus, directory choices, map options and settings immediately. Cancel restores the original language. Resource lookup uses the explicitly selected culture, and indexer bindings receive collection reset notifications. Names, scripts, descriptions and transcript content are not translated.
- Final Release suite: 417 passed (262 core, 155 desktop), zero failures. Regression coverage includes SQLite ANSI round trips, validation/backward compatibility, indexed versus truecolor identity, live recoloring of existing runs, independent section scrolling, live language switching and cancellation, and preserving a connected session/transcript/input draft while translating menus and controls.
- Inspected rendered English and German MUD-color settings at `artifacts/screenshots/settings/ansi-colors.png` and `ansi-colors-de.png`. Localization facade check passed. Native Windows/Linux interaction remains untested; the current client session was not restarted.

## Dedicated terminal and Avalonia 12 · September 17, 2026

- Release suite: **427 passed** (262 Core, 165 desktop), zero failures. Localization facade check passed. Desktop tests migrated to xUnit 3 / Avalonia Headless 12 and explicit HarfBuzz initialization.
- Upgraded Avalonia to 12.1.2, Dock to 12.1.0.6, AvaloniaEdit to 12.0.0; integrated Iciclecreek.Avalonia.Terminal 4.0.2 / XTerm.NET 2.0.2 with a required injected display factory. No PTY process is launched.
- Regression tests cover cursor overwrites, explicit clearing, split server erase controls, charset designation, partial ANSI around local echo, preservation of the server saved cursor and charset, output before attachment and while detached, bounded scrollback, palette changes versus literal RGB, no-PTY copy/select-all, blink pixel changes without terminal focus, and preview/cancel of blink/font settings.
- Existing loopback login, script, mapping, directory, persistence, localization, theming, docking, and menu tests pass. ANSI and demo screenshots were inspected under `artifacts/screenshots/avalonia12`; a dedicated terminal screenshot is `artifacts/terminal/dedicated-terminal.png`.
- Local replay observation: 1,800 retained lines plus 12 batches of 10 lines, 900×500 headless Skia. Incremental display feed/layout/forced rendering: **14.3 ms total**; reconstructed prior full-inline rebuild: **5316.5 ms total**. This is a single synthetic run, not a general FPS/throughput guarantee; no network or native window/compositor is measured. Reproduce with `dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~TerminalReplayTests`; results go to `artifacts/terminal/replay-timing.json`.
- Remaining limits: NAWS stays at the existing 100×40 advertisement; dynamic resize reporting and inline terminal image protocols are not added by this integration. Tests ran on macOS; native Windows/Linux runtime checks remain outstanding. Existing running user sessions were not restarted.

### Form-based macros (2026-09-17)

- Added Play / Macros / Scripts / Diagnostics footer navigation and a reusable macro editor with text conditions, exact aliases, timers, function keys, ordered command lines, save/delete, and enable controls.
- Structured rules compile into the existing isolated JavaScript runtime. Tests cover literal escaping, ordered actions, exact alias consumption, timer scheduling, shortcut dispatch, disabled defaults, private-input pausing, saved-versus-draft execution, session isolation, and rejected invalid saves.
- SQLite schema 2 adds nullable macro metadata while preserving existing scripts; migration, reopen, world isolation, mismatched source rejection, and deletion were verified against a version-1 database.
- Macro UI verified in all five languages, including changing language without losing selected conditions. Headless captures: `artifacts/screenshots/macros-*.png`.
- Release verification: Core 289 passed; Desktop 201 passed. Localization generated-resource check passed.

### Sectioned connection configuration and live automation menu (2026-09-17)

- Replaced the fixed-size connection form with a resizable 1100×780 window, collapsible 160-pixel section navigation, and Connection / Login / Scripts / Macros sections. Apply preserves the window and profile ID while saving connection/login fields. New worlds unlock offline automation editing after being saved.
- Reused the script and macro editors in configuration; section switching preserves drafts and undo history. Profile automation is created through an injected factory whose libraries cannot launch workers. The script area fills over 65% of window height, and collapsing navigation adds 160 pixels of width.
- Removed Scripts/Macros footer tabs. Play and Diagnostics remain, with a compact Automation menu for session-local switches, runtime status, disable-all, explicit reload, and opening the originating world's configuration. Reload adopts saved rules/defaults without changing other connections.
- Release Desktop suite: 208 passed. Added two further integration checks for the actual popup and originating-profile selection with duplicate TCP connections; the final affected set passed 13 tests. Localization resource-generation check passed. All five profile editor language captures were rendered; English layout visually inspected.

## Session footer and selectable agent goals (2026-09-18)

- Split live controls into Scripts (individual checkboxes), Macros (session-local master toggle preserving saved defaults), and Agent (goal checklist, Play/Stop, visible status).
- Goals are saved in the existing SQLite agent profile payload; older single-goal text is retained as one goal. The settings editor supports adding, editing, deleting, and choosing startup defaults. Live selections remain local to each connection.
- Checked goals are combined into one bounded objective. Changing the selection cancels pending work. The command catalog still constrains available actions.
- Regression tests cover macro isolation and reload, selected-goal requests, cancellation without stale sends, migration and persistence, and footer layout at 520 and 900 pixels. Headless captures inspected for the goal popup, settings editor, and footer.
- Verified: 325 Core tests and 241 Desktop tests passed. Localization generation check passed for all five languages. No live game commands were sent for this change.

## Single goal, local templates, and Markdown editors (2026-09-18)

- Each connection selects one goal with radio buttons; changing the selection stops pending work and clears previous working memory. Core composition rejects multiple active goals, and only one saved default is permitted. Older multi-default profiles retain all goals and select the first.
- Goals now persist a name, Markdown description, and Markdown rules in the existing SQLite payload. Legacy description text is retained. Description and rules use AvaloniaEdit with a Markdown grammar and two-way view-model bindings.
- Local, independently editable templates: Observe surroundings, Explore carefully, and Review inventory. Templates never start an agent or change its command catalog. Markdown rules are model instructions; existing command permissions and execution limits remain enforced separately.
- Verified 327 Core and 243 Desktop tests (570 total), including migration, single selection, request contents, template isolation, Markdown highlighting, and editing/switching between goals. Localization generation check passed. Inspected headless screenshots of both Markdown tabs and the goal settings layout. No live game commands were sent.

## Connection dialog drafts and one Save/Cancel flow (2026-09-18)

- Save world now commits connection, login, scripts, macros, and agent edits from any section. Section-level save buttons and shortcuts are hidden in this dialog; Cmd/Ctrl+S saves the whole connection. New connections can use all editors before their first save.
- Offline script/macro additions, deletions, and enable changes are buffered until saving. Agent settings and API-key edits remain drafts. All changed sections are validated before persistence begins; storage failures leave the dialog open for retry. The settings, automation, and credential stores remain separate persistence operations, not a cross-store transaction.
- Cancel, Escape, window close, New, and switching saved worlds protect pending changes with Keep editing / Discard changes. Reverting fields to their baseline removes the dirty state. Connection removal is staged until Save.
- Add template uses the application button style and a dropdown chevron. Rendered the settings dialog and discard confirmation; captures are in artifacts/screenshots/connection-drafts.
- Release build: zero warnings/errors. Full suite: 327 Core and 251 Desktop tests passed (578 total). Localization facade check passed. Regression coverage includes multi-section saves, unselected rules, discard and keep-editing paths, invalid settings, new profiles, world selection, reverting edits, and failed-save retry. Existing native sessions were not restarted.

## Broad protocol discovery and structured login (2026-09-18)

- Native MSDP and MSDP-over-GMCP discover reportable variables, subscribe to custom fields, deduplicate requests, batch reports and reset on renegotiation. GMCP advertises common data modules while retaining unrecognized incoming packages.
- Diagnostics now includes an observed field inventory with stable fingerprints, bounded paths and no scalar values. Tests cover partial updates, ordering, changing values, arrays, schema changes, malformed/truncated input, privacy, history eviction and Clear. Rendered Messages and Observed fields in `artifacts/screenshots/protocol-discovery/` and inspected the fields view.
- GMCP `Char.Login 1` uses the existing saved credentials and auto-login setting. Local TCP fixtures verify one-shot credentials, escaped Unicode passwords, successful and rejected results, disabled/unsupported fallback, late offers after text login begins and private authentication packets. OAuth and reconnect tokens are not implemented.
- Mapping service jobs and a manual field picker remain a proposal in `docs/proposals/protocol-mappings.md`; no diagnostic upload was added.
- Final Release build: zero warnings/errors. Full regression suite: 348 Core tests and 257 Desktop tests passed, 605 total. Localization generation check passed. Tests used local fixtures; no live MUD credentials or sessions were used.

## Readable diagnostics during login (2026-09-18)

- Replaced blanket diagnostic suppression during login/private input with a separately sanitized diagnostic copy. Login offers/results and other structured protocol data remain readable; raw traffic still follows the original script/agent privacy gates.
- Redacts credential fields recursively, masks credential/token packages, and scrubs exact echoes of known saved or manually submitted private credentials. Malformed or truncated private payloads remain masked. Redacted/authentication data does not enter the field inventory.
- Tests cover nested GMCP secrets, MSDP password fields beside readable vitals, private malformed packets, saved and manual password echoes, login-time vitals, readable error responses, bounded history and UI rendering. Inspected `artifacts/screenshots/login-diagnostics/login-diagnostics.png`.
- The first full run exposed two existing preferences tests using ordinary xUnit worker threads despite broadcasting global UI-language changes on disposal. Both now use Avalonia's UI-thread test attribute, consistent with the rest of their test class.
- Final Release build passed with zero warnings/errors. All 615 tests passed (357 Core, 258 Desktop). Localization generation check passed. No live MUD credentials were used.

## Icesus frozen-night theme (2026-09-18)

- Sampled computed colors and corner radii from the public Icesus web client. Added `directory-server/themes/icesus.json` and exact normalized `play.icesus.org` enrichment for cached listings and future imports.
- Updated the local server snapshot and desktop SQLite catalog after backing them up. Preserved other listing fields, artwork and server cache age. Restarted only the directory service to load the enrichment rule for future imports, and confirmed its `/directory` endpoint returns `icesus-frozen-night-v1`.
- Rendered and inspected `artifacts/screenshots/icesus/icesus-workspace.png` using sample text and a local TCP fixture. Verified an existing connection adopts the catalog theme without changing the user's personal theme. This is a palette for the existing layout; it adds no logo, landscape or vital widgets.
- Release build passed with zero warnings/errors. All 616 .NET tests passed (357 Core, 259 Desktop), along with 27 backend tests and the localization generation check. No live MUD login or gameplay commands were used.

## Shared state, anonymous discovery and Azure mappings (2026-09-18)

- Added framework-independent `Wandur.Models` for character, opponent, vehicle and world state; exact protocol field bindings; bounded validation; evidence and schema fingerprints. The .NET worker and desktop client share these contracts. The Python directory API overlays validated mapping publications without changing provider cache age or artwork.
- The worker probes GMCP/MSDP anonymously with public-address validation, bounded concurrency and packet limits. It persists daily eligibility, model budgets, resumable batches and last-good mappings. Runtime client bindings handle partial updates, absent maxima, invalid numeric values, endpoint changes and character/session resets. UI vital widgets and a manual mapping editor remain future work.
- Release build passed with zero warnings/errors. Full solution run passed 676 tests (390 Core, 262 Desktop, 24 worker). A subsequent live-response regression test increased the worker suite to 25, all passing; the unchanged Core/Desktop suites were not rerun. Backend: 42 tests passed. Localization generation check passed. Independent review identified four defects that were fixed with regression coverage and rereviewed.
- Live initial survey checked 221 connectable listings: 48 exposed usable GMCP/MSDP evidence, 159 exposed none within the 15-second window, 6 timed out, 6 failed connections and 2 reached limits. Missing evidence is not proof of absent protocol support. Anonymous Jedi discovery produced 94 observed/advertised fields and a 67-binding Azure mapping.
- Model output sometimes assigned multiple sources to one destination. The generator now omits all conflicting suggestions while preserving unrelated valid suggestions. Malformed bindings and invented sources still reject the response. The regression test passed and independent review found no correctness issues in this adjustment. The prompt/generator version changed, so old completed maps are eligible for a bounded refresh.
- After retrying rejected proposals, the API publishes 48 maps containing 1,044 bindings: 45 Azure-assisted and 3 deterministic fallbacks. Three generated proposals remained invalid and will retry on the daily schedule. These mappings remain provisional; structural validation does not establish semantic correctness.
- Worker-recorded initial usage, including recovery attempts: 59 requests, 127,333 input tokens (6,612 cached) and 36,255 output tokens. Two additional diagnostic requests used 6,075 input and 2,010 output tokens. At the published Global Standard short-context rates, combined estimated inference cost was about $0.0714, not an Azure invoice.
- Configured the existing Azure resource and `gpt-5.6-luna` deployment independently of image generation. Reduced the temporary 250-call initial budget to 20 calls per UTC day. Installed `net.wandur.discovery` as a macOS user launch agent with a 24-hour interval; verified registration and initial exit code 0. It requires the Mac and workspace volume to be available. The initial historical call count remains above the reduced cap; further calls wait for the next budget day.
- Rebuilt `artifacts/macos/Wandur.app`. Restarted the local directory API and verified its published maps. Backed up the desktop SQLite database and overlaid mappings for 48 cached worlds, preserving the Icesus theme and other listing data. Existing desktop sessions were not restarted; reopen the updated app to load the new assemblies.

## Requested MSSP survey (2026-09-18)

- Ran a separate one-time anonymous Telnet option-70 survey of all 221 connectable listings, deduplicated to 214 endpoints. Nineteen listings lacked a usable Telnet endpoint. Used eight concurrent probes, 15-second deadlines, public-address checks, bounded input and no login or gameplay commands. Parser checks covered fragmented negotiation, repeated values, duplicate negotiation, rejected unrelated protocols and oversized values.
- Results by listing: 115 reported MSSP (108 unique endpoints), 14 explicitly declined, 76 stayed silent, 6 timed out before connection, 6 failed connections and 4 closed without MSSP. A silent or failed probe does not establish lack of support.
- Icesus returned 37 fields and 45 players; Legends of the Jedi returned 58 fields and 96 players. Jedi's `UPTIME=96` does not represent a credible Unix startup timestamp under the specification. These are self-reported point-in-time observations, not average populations or guaranteed accurate metadata.
- Saved readable results, CSV and complete response fields under `artifacts/discovery/mssp-survey.md`, `mssp-survey.csv` and `mssp-report.json`. The throwaway survey script remains in `/tmp`; this survey does not add MSSP to the installed daily GMCP/MSDP worker and incurred no model calls.

## Live mapped resource bars (2026-09-18)

- Added a compact resource strip above the command composer. It reads the connection's normalized character, opponent, vehicle and world resources, plus progression entries with a supplied maximum. Health, mana, movement, shields, hull and other mapped pairs appear as labeled current/max values with colored progress bars.
- Only finite nonnegative current values with positive finite maxima render. Missing maxima, invalid values and unpaired levels produce no bar; zero current remains visible. Fill is clamped to 0–100 while text retains actual values. The strip hides on disconnect or when no valid pairs exist and clears naturally on reconnect through the existing state reset.
- Adaptive columns and a bounded vertical scroll area preserve terminal space on narrow windows. Common labels and entity prefixes support all five UI languages. Unchanged snapshots retain the rendered cards rather than rebuilding on every transcript event.
- Verified local TCP GMCP packets create a bar only after the maximum arrives, update it incrementally, honor the existing privacy gates and disappear on disconnect/reconnect. Rendered and inspected narrow, wide and integrated-session screenshots under `artifacts/screenshots/resource-bars/`, using simulated data rather than a live player account.
- All 681 tests passed: 390 Core, 266 Desktop and 25 discovery. Localization facade check passed; independent code review found no actionable issues. Rebuilt `artifacts/macos/Wandur.app`. The running application and MUD sessions were not restarted. Bars require a valid saved/catalog mapping and corresponding live values; this change does not create mappings for previously unmapped worlds.

## Icesus curated mapping and automatic directory refresh (2026-09-18)

- Fixed the `SessionTests` MSDP refresh fixture (missing `Label`) and aligned two Desktop tests with the always-fetch-on-startup catalog behavior: the offline browser test now expects the saved-worlds warning after a failed startup refresh (matching `WorldCatalogTests.FreshAndExpiredCachesSurviveFailedStartupRefresh`), and the world-browser fixture uses an offline HTTP handler so tests no longer read a live local directory. Release suites: 25 discovery, 420 Core, 285 Desktop, all passing with the local directory API running. Localization facade check passed. Backend suite unchanged at 44 passed.
- Verified `/directory` on port 8765 publishes Icesus with nine bindings, and the client cache and saved Icesus profile each carry those nine bindings.
- Live Icesus login was not exercised: verification used the official Mudlet package definitions plus simulated protocol packets. One app restart is required to load the rebuilt executable; afterwards directory data refreshes periodically and validated mappings update active sessions. The discovery worker was not reinstalled; rerun `scripts/install-discovery-worker-macos.py` to pick up the changed protocol support advertisement.

## Room terrain inference (2026-09-18)

- Added optional local room-terrain inference: the room-classifier model package (fine-tuned MiniLM over ONNX Runtime, downloaded on demand or installed from a file, verified by manifest hash) classifies rooms that arrive without terrain. Results persist on the map as inferred fields and paint the same palette colors, with provenance and confidence in tooltips and the room editor. Server terrain and manual edits take precedence; inference never touches exits, identity or routes, pauses during map walks, and stops when the setting is turned off.
- Parity: the C# preprocessing reproduces all 23 package fixtures' texts, and the full ONNX chain reproduces every fixture's token ids, probabilities and predictions against package 0.1.1 (gated test, run in Release).
- Controller scheduling verified with a fake classifier: rooms with server terrain are never classified, abstentions are recorded so they are not retried, stale results are rejected, results apply on the UI thread without setting the manual-edit flag, the current room is classified first, and a real loopback-session walk is never interrupted.
- Added an auto-center toolbar toggle (on by default, persisted) that keeps the current room centered as it changes.
- Rebuilt `artifacts/macos/Wandur.app`; the bundle contains `runtimes/osx-arm64/native/libonnxruntime.dylib`. The model package is installed under the app data folder so the rebuilt app starts with inference ready. Model download from the room-classifier GitHub release requires the v0.1.1 assets to be published; until then use "Install from file" with the locally built package zip.
