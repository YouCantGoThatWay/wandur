# Client architecture

## Dependency injection and ownership

`App` is the composition root. Microsoft.Extensions.DependencyInjection registers one `ClientDatabase` and the settings, map, script, directory-cache, and protocol-history stores that use it, plus one singleton `IPasswordVault`; vault registration selects macOS Keychain, Windows Credential Manager, or Linux Secret Service. `MainWindow`, `SessionWorkspace`, and each `WorkspaceController` require those services by constructor injection. Consumers never resolve services from a global container and have no fallback constructor that creates a vault.

## Local persistence

`ClientDatabase` owns `wandur.db`, enables foreign keys and WAL, applies the schema inside a transaction, and exposes short-lived read connections plus serialized write transactions. World identity is stable across endpoint edits: profiles resolve an endpoint to a world ID, while maps and scripts are keyed by that world ID. Raw endpoint aliases are retained so legacy filenames using a trailing dot, Unicode spelling, or another equivalent host form can still be imported later.

The SQLite stores normalize searchable map fields (`area`, floor, coordinates, room IDs and directed link endpoints) and retain validated payloads for fields that evolve. Directory descriptions and artwork are cached in the same database. Historical GMCP/MSDP capability observations carry timestamps and are never treated as current negotiation evidence. Passwords and transcript content are intentionally outside the database.

Migration is lazy and idempotent. Existing files are read and validated before their rows are committed; originals are never deleted or rewritten. Per-source import markers and script tombstones prevent a later endpoint alias from resurrecting a deleted script or map. Use SQLite's backup API for future live backups when WAL sidecars exist; copying only `wandur.db` while the client is running is not a consistent backup.

Sessions have independent connection lifetimes. `SessionWorkspace` creates/owns the controllers for dynamic tabs and disposes them on tab/window close. Window composition creates view models with the current session and directory dependencies. The DI container does not retain disposable transient controllers after their tabs close.

## MVVM boundaries

- `ProfileEditorViewModel` owns editable fields, address normalization, cancellable directory suggestions, validation, save/remove commands and busy/error state. It uses `IWorldProfileStore`; `ProfileDialog.axaml` binds properties/commands. Code-behind only handles window lifetime and focus normalization.
- `PreferencesViewModel` uses `IClientSettingsStore` and an injected appearance-preview callback. `OptionsDialog.axaml` contains the presentation and bindings. Cancel restores the previous appearance; save preserves other settings changed by another session.
- `WorldLibraryViewModel` owns saved-world selection and add/edit/connect commands. Its view formats rows and handles pointer/key gestures.
- `WorldBrowserViewModel` owns directory queries, facet options, filtering/sorting, result selection, count/status/feedback and save/connect commands. Its view owns controls, images, layout and external-link launching.
- `WorkspaceController` remains the session presentation/application adapter: connection lifecycle, bounded output queue, prompt/private-input state, transcript/history and login coordination. It does not construct controls. An injected `ITranscriptDisplayFactory` creates the session-owned `ITranscriptDisplay`; the controller disposes it. The display adapter owns terminal emulation, rendering, scrolling and copy gestures. Core remains GUI-independent and retains its lightweight ANSI model for prompt/transcript analysis.
- `MainWindow` and `DesktopMenus` own native window/menu/dialog integration. OS dialogs, clipboard and Avalonia controls stay in the presentation layer.

Use CommunityToolkit.Mvvm for new observable properties and commands. Do not put persistence, networking, query algorithms or credential access in a view. Parameterless AXAML constructors exist for Avalonia's loader/designer; runtime composition passes a populated view model.

## Credentials and login

A world can save a username and opt into password storage and automatic login. JSON contains only the username, random password reference and prompt patterns. Vault keys bind the world ID, password reference, host, port, TLS choice and username. Changing those details requires re-entering the password; changing a display name does not.

- macOS: Security.framework generic-password APIs, without a shell.
- Windows: native `CredReadW`, `CredWriteW`, `CredDeleteW`, generic credentials persisted for the current user.
- Linux: `secret-tool` and a running Secret Service provider such as GNOME Keyring or KeePassXC. Install `libsecret-tools` on Debian/Ubuntu, or the package providing `secret-tool` on other distributions. Password input goes through stdin, never process arguments. A missing/locked store produces a readable error; there is no plaintext fallback.

Auto-login supports GMCP `Char.Login 1` password credentials when the server offers that method before the text handshake begins. It reads the existing endpoint-bound vault entry and sends credentials once. Missing credentials, disabled auto-login or unsupported methods receive `Char.Login.Credentials {}` to hand control back to the server. A rejected or unanswered structured attempt stops automation rather than retrying through text; the result timeout is 30 seconds. OAuth/browser login and reconnect tokens are not implemented or advertised. Incoming `Char.Login.*` frames remain private to scripts and the agent independently of echo state. Diagnostics separately shows redacted login offers/results and other incoming structured data during login.

The fallback is a one-shot, two-minute name/password handshake with configurable regex prompts. ANSI is parsed before matching; split packets and trailing newlines are supported. Sending a manual command cancels automatic login. Credentials bypass local echo/history. Normal Telnet still sends data unencrypted over the network; TLS must be supported by the MUD to encrypt transport. Account menus, MFA and other extra login steps remain manual.

New passwords use a new vault reference. If settings persistence fails, that newly created entry is removed. Replaced/forgotten passwords are deleted after settings save; cleanup failures produce a notice. The native macOS adapter is exercised with temporary credentials. Windows/Linux native stores still need execution on their respective systems.

## Localization

All client-owned natural-language labels, help, statuses and validation messages use `Wandur.Core/Localization/Strings.resx`, a strongly typed facade, and .NET ResourceManager satellite assemblies. Languages: English fallback, Spanish, French, German and Brazilian Portuguese. Preferences stores a language code; blank follows the system language. Unsupported system languages use English. Changes preview immediately, without interrupting connections. `UiLanguage.Culture` holds the selected resource culture explicitly so captured async execution contexts cannot freeze labels in an old language. `LocalizedText` provides key-based Avalonia bindings and emits a collection reset when the language changes; preferences restores the prior culture on Cancel. View models refresh computed labels through scoped subscriptions.

Server transcripts, externally supplied world descriptions/tags, player names, protocol commands and technical identifiers retain their source text. The offline demo is sample game content, not translated client chrome. Theme storage keys are stable while their display names are localized.

To change strings:

1. Add a descriptive resource key and a complete English sentence in `Strings.resx`.
2. Add all four translations. Preserve format arguments/specifiers and intentional newlines. Use separate singular/plural resources rather than concatenating English suffixes.
3. Run `python3 scripts/generate-localization.py` to regenerate the typed facade.
4. Reference `Strings.Key` from computed view-model values. For live UI labels use `Ui.TextKey(nameof(Strings.Key))`, `Ui.ButtonKey(...)`, `LocalizedText.Binding(...)`, or `{ReflectionBinding [Key], Source={x:Static loc:LocalizedText.Instance}}` in AXAML. Avoid capturing translated labels as immutable control values. Use `Strings.Format` for parameters; never localize protocol tokens or automation IDs.
5. Run tests. Resource tests require exact key and format-token parity and localized-dialog tests render all locales. `python3 scripts/generate-localization.py --check` detects facade drift.

The initial translations have automated coverage and layout checks; they have not been reviewed by professional translators.

## Mapping

`WorkspaceController.Mapping` coordinates room observations and movement evidence for each session. Its `RoomMapTracker` and protocol/text decoders live in Core. `IRoomMapStore` is injected from startup through the window and session factory; `MapViewModel` owns presentation and an isolated offline recognition exercise. See [the mapper guide](mapper.md) for supported formats and inference limits. `MapViewModel` partials separate editing and navigation commands; `RoomMapControl` draws a north-up viewport and forwards explicit edit gestures. Core owns validated JSON, directed weighted routes, map revisions/deletion tombstones, merge aliases and bounded undo. `WorkspaceController.Navigation` binds a verified walk to one session and its privacy epoch, with room acknowledgements queued beside transcript output before the next movement is sent.

### Room terrain inference

`Wandur.Core.Classification` runs the room-classifier model package locally: `RoomTextPreprocessor` reproduces the package's `preprocessing_spec.json`, `OnnxRoomEnvironmentClassifier` runs the encoder through ONNX Runtime one room at a time and applies the logistic head, and `ModelPackageInstaller` downloads/verifies packages by manifest hash into `models/room-classifier/<version>/`. `WorkspaceController.Inference` queues rooms without terrain onto a background worker and applies results on the UI thread through `RoomMapTracker.ApplyInference`, which stores `InferredEnvironment`/`InferredConfidence`/`InferredKey` on `MapRoom`. Presentation precedence lives in `MapEnvironmentPalette`: server or manual terrain first, inference second.

### Themes

`UserTheme.FromPreset` owns the shipped presets and `UserTheme.PresetNames` drives the picker, so a new entry appears in preferences, in duplication and in the tests without further wiring. Six are dark (Ember, Moonlight, Forest, Midnight, Slate, Rose) and four are light (Paper, Parchment, Daylight, Linen); `IsLight` selects the Fluent variant. Each preset supplies every key in `UserTheme.ColorKeys` plus a complete terminal palette for indices 0 to 15, and `UserTheme.DisplayName` maps the stable identifier to its localized name.

A world theme belongs to an open session. `SessionWorkspace.ApplyAppearance` reads the active tab's `WorkspaceController.WorldTheme`, so a world theme arrives when its session opens, follows the active tab, and gives way to the personal theme as soon as no active session carries one. Highlighting a row in the directory, in the saved world list or in the toolbar picker never changes the appearance; the preferences dialog still previews the personal theme live through `PreviewAppearanceSettings`.

A theme change writes no new resource entries. Every write to `Application.Resources` raises a resources-changed notification that re-evaluates every DynamicResource in the tree, which measured about one millisecond per key and about seventy milliseconds per switch. `ThemeResources` keeps one brush per key for the life of the process and only moves its color, which leaves the resource dictionary untouched and repaints through the brush's own invalidation; the Fluent palettes, the theme variant and the dock grip drawing are rewritten only when they actually change. A headless switch with a session on screen now costs a few milliseconds. Because the bound properties no longer change identity, `ThemeService.Applied` tells consumers that read brush colors, notably `TranscriptDisplay`, to rebuild once and repaint.

`ThemeContrastTests` runs over every preset and enforces the readability rules: WCAG 2.x contrast of at least 4.5:1 for ANSI indices 1 to 15 against the preset's terminal background and at least 3:1 for index 0, main text at least 7:1 against the shell, muted text at least 4.5:1 and the accent at least 3:1. Hue identity is checked too, within 25 degrees of one canonical hue per role (red 0, green 140, yellow 55, blue 218, magenta 297, cyan 190), and each bright color has to stay visibly apart from its normal counterpart. Light presets darken and saturate their terminal colors, and their indices 7 and 15 become dark greys that keep the default-text role. A preset that fails these rules fails the build rather than shipping unreadable colors. The same test also checks the fallback palette against every preset terminal background of its own lightness, which is why Ember and Linen are the two it draws from.

### ANSI palettes

A preset's sixteen colors are chosen for that preset's own terminal background. A world theme, or a custom background color from the terminal preferences, can invert that lightness, and the preset palette would then be illegible: `ThemeService` therefore falls back to `UserTheme.PaletteForBackground`, which returns Ember's palette for a dark background and Linen's for a light one. A custom theme keeps the colors its editor shows, including over a world theme.

`TextStyle` retains the ANSI index alongside its default RGB value. The terminal adapter binds palette resources to XTerm theme options, so changing themes recolors existing output without replaying network data. Explicit RGB colors have no index and remain literal. Each `UserTheme` stores an optional dictionary of overrides for indices 0–15; omitted entries use `AnsiPalette.Defaults`, so older saved themes remain valid. Personal ANSI overrides remain active when a world supplies its own chrome theme. Palette drafts are deep-copied at load, duplicate and save boundaries.

## Dedicated terminal display · September 17, 2026

Avalonia 12.1.2, Dock 12.1.0.6, AvaloniaEdit 12.0.0, and Iciclecreek.Avalonia.Terminal 4.0.2 are pinned in package lock files. Desktop headless tests use xUnit 3 as required by Avalonia 12; Core tests remain xUnit 2.

`AnsiTerminal.OutputAppended` distinguishes server output from literal local echo. Its explicit `Cleared` event resets the display on user clear/reconnect; parsed server erase sequences do not fire it. `TranscriptDisplay` feeds each chunk once into the session's terminal, including while detached. Display buffers retain 2,000 scrollback rows plus the viewport; exported text comes from that buffer and joins soft-wrapped rows. The separate command field, scripting privacy gates, protocol handlers, and mapper keep their existing ownership.

`TerminalStreamFramer` retains incomplete controls (bounded at 8 KiB) so local echo cannot finish a fragmented server escape. Local echo preserves server attributes and the server's saved cursor. The embedded control has no process/PTY, disallows server clipboard and window operations, and currently disables terminal image protocols. We supply copy/select-all shortcuts because upstream's input path assumes a PTY.

`MudTerminalSurface` initializes the engine before the first visible tab, retaining output that arrives early. Text blink is default-off and independent of the separate input's cursor. Its visible-only timer temporarily conceals blinking cells for rendering and restores attributes immediately, preserving selection/export and server state. This adapter uses the default Avalonia rendering path and UI-thread writes; enabling upstream's threaded PTY or direct Skia path would require reviewing that assumption.
