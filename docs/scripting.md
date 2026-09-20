# Macros and world scripts

Each MUD has saved script and macro definitions. Right-click a saved world and choose **Edit**, or use the active connection’s **Scripts → Edit configuration…** menu. The connection settings window has **Connection, Login, Scripts, and Macros** sections. **Session → Scripts…** opens configuration for the active connection’s world.

The window is resizable. Hide the section list with the top-left toolbar button to widen the editor. The script editor keeps its own compact toolbar, optional output, and API help; the code area fills the available height. Switching sections retains drafts and editor undo history while the window stays open.

Configure a new or existing world in any section, then choose **Save world** to save connection, login, scripts, macros, and agent settings together. All edits, including adding, deleting, and enabling rules, stay in a draft until you save. **Cancel**, closing the window, or switching worlds asks before discarding pending changes. Switching sections keeps your draft. Cmd/Ctrl+S saves the entire dialog.

## Live controls

The active session has **Play** and **Diagnostics** footer tabs. Its **Scripts** menu lists hand-written scripts with individual enable switches and runtime status, a refresh icon to reload saved rules, and **Edit configuration…**. The separate **Macros** footer toggle switches enabled macros on or off for this connection without changing their saved defaults.

Live enable switches affect only that open connection and do not change saved startup preferences. Editing or enabling definitions in configuration does not execute code there or change already-open connections. Choose **Reload saved rules** in a connection to replace its loaded definitions and apply saved enable preferences. This restarts its scripts; it does not interrupt another connection. New connections load saved definitions and enable preferences normally.

## Macros: no code required

The **Macros** section offers forms for four rule types:

| Type | When it runs |
| --- | --- |
| Text received | A complete incoming line contains, starts with, or equals your text. |
| Command alias | The entire command you enter equals your alias; the replacement is sent instead. |
| Repeating timer | Every configured interval, between 1 second and 24 hours, while enabled and connected. |
| Keyboard shortcut | F1–F12 while the Play command input has focus. Some keyboards require Fn. |

Use **+** to create a disabled macro, give it a name, select its condition, and enter one command per line. Turn on **Enabled** if desired, then choose **Save world**. Matching text is literal, with optional case-insensitive matching; regex and capture substitutions remain available in Scripts. A macro sends up to 20 commands in order. Timers wait a full interval after activation and do not replay missed intervals in a burst. Matching occurs on complete received lines, not unfinished prompts.

For example, choose **Text received → Contains**, enter `You are hungry`, and send `eat bread`. Adjust commands and text to your world.

The forms store structured definitions alongside scripts in the world's SQLite library. They generate JavaScript internally and use the same isolated engines, command gateway, rate limits, and private-input pause behavior. Macros are listed separately from hand-written scripts. New macros start disabled. The configuration checkbox saves the startup preference; reload or open a new connection to run that saved rule. Switching views leaves enabled macros running; closing the session stops them. Runtime errors appear in the world configuration.

Definitions and saved enable preferences currently follow the same sharing rules as scripts below. Duplicate open connections have separate worker processes; a saved change does not replace another already-open connection's loaded definitions. Macros and scripts share the 64-entry library limit. Shortcuts do not run in editors or during private input. `Events.Key` is used internally by generated shortcut macros and receives the selected function-key name as `text`.

## Workflow

1. In configuration, select **Scripts**, choose **New**, give the script a name, and edit its source.
2. **Save world** stores all edited rules in this world’s library along with the other connection settings.
3. Switch **Enabled** on to save its startup preference. Configuration never launches a worker.
4. In the active connection’s **Scripts** menu, use the refresh icon (**Reload saved rules**) to adopt your changes.
5. Use the live enable switch to start or stop a loaded rule for just that connection.

New scripts and scripts migrated from the original single-script editor start disabled. Closing the connection stops every script and ends the session's worker process. A script error stops only that script's engine; correct and save its definition, then reload, or toggle its live switch off/on to retry. Private input and automatic login pause automation.

Saved definitions are shared by world identity. Saving merges the edited rules into the on-disk library. Already-open connections retain their loaded definitions until explicitly reloaded. Deleting a rule from configuration requires an inline confirmation and removes its saved definition when you choose **Save world**; reload active connections to remove their previously loaded copy.

The editor includes syntax highlighting, light/dark colors, line numbers, indentation, undo/redo, local output and API examples. Type `mud.` or `Events.` for completion suggestions, or press **Ctrl+Space**. **Tab/Enter** accepts and **Escape** cancels. Event callback parameters receive field suggestions (`text` for Line, `package` and `data` for Gmcp, `variable` and `value` for Msdp). Suggestions show signatures and descriptions. This is focused API completion, not full JavaScript type checking or a language server; dynamic GMCP fields depend on the MUD. **Cmd+S** on macOS or **Ctrl+S** on Windows/Linux saves. JavaScript is embedded; no Python or Node.js installation is required.

## Events

`Events.Line`, `Events.Gmcp` and `Events.Msdp` are named JavaScript constants on a frozen global object. They are not a TypeScript enum. The object and its values cannot be replaced or extended. Existing scripts using `"line"` or `"gmcp"` strings continue to work.

```js
// Called for each newly completed server line, with ANSI styling removed.
mud.on(Events.Line, event => {
    if (event.text.includes("You are hungry")) {
        mud.send("eat bread"); // Adjust to commands supported by your MUD.
    }
});

// Structured data, only when the server supplies GMCP.
mud.on(Events.Gmcp, event => {
    if (event.package === "Char.Vitals" && event.data) {
        mud.echo("Vitals: " + JSON.stringify(event.data));
    }
});
```

`mud.on(Events.Line, callback)` receives `{text}`. `mud.on(Events.Gmcp, callback)` receives `{package, data}`; `data` is parsed JSON, or `null` when the message contains only a package name. Malformed GMCP messages are ignored. Line subscribers run in registration order before regex triggers. Each script has its own globals and its own engine inside the session's worker process; sending a command does not create another command event or alias invocation.

### Events.Msdp

```js
// One event per MSDP variable update, only when the server supplies MSDP.
mud.on(Events.Msdp, event => {
    if (event.variable === "SHIPHULL") mud.echo("Hull: " + event.value);
    if (event.variable === "AFFECTS") mud.echo("Affects: " + event.value.join(", "));
});
```

`mud.on(Events.Msdp, callback)` receives `{variable, value}`. The payload is decoded with the same MSDP reader the mapper uses, so a script sees exactly what the client sees. A plain MSDP value arrives as a string, an MSDP array as a JavaScript array and an MSDP table as an object. MSDP carries numbers as text, so a numeric-looking variable such as `SHIPHULL` stays a string; convert it with `Number(...)` when you need arithmetic. At most 64 variables from one payload become events, and a value whose JSON exceeds 8192 characters is skipped. Malformed payloads are ignored rather than reported as script failures, and private intervals are excluded exactly as they are for GMCP.

## Current values: mud.state

Each engine keeps the latest value of everything its script has received, so a panel or a trigger can read a value without having cached it itself.

```js
mud.state.get("gmcp.Char.Vitals.hp");   // the newest Char.Vitals hp, or undefined
mud.state.get("msdp.SHIPHULL");         // the newest SHIPHULL value, or undefined
mud.state.snapshot();                   // { gmcp: {...}, msdp: {...} }
```

A path is `gmcp.<Package>.<field>...` or `msdp.<VARIABLE>`. A GMCP package name is split on dots, so `Char.Vitals` is stored under `gmcp.Char.Vitals`. An unknown path returns `undefined`, never `null`. Objects and arrays are returned as copies, so changing what `get` or `snapshot` handed you does not change the cache.

The cache is fed by the same privacy-gated events a script subscribes to, so nothing private can enter it. It holds at most 512 entries per protocol and 256 KiB per protocol; a value larger than 32 KiB, or an update that would exceed those bounds, is dropped and the previous value is kept.

The client also keeps this cache on its own side for the whole session, including values that arrive during automatic login, before any script is running. When a script starts, and on every restart, that cache is sent to the session's worker ahead of the load whenever it has changed since the last seed, and every engine starts from it before the first line of its script runs, so `mud.state.get` answers immediately and the usual pattern of subscribing with `mud.on(Events.Msdp, refresh)` and calling `refresh()` once at load shows values right away. Seeding fires no `Events.Gmcp` or `Events.Msdp` callbacks. A world sends a reported MSDP variable once and then only when it changes, which is why a script that started later would otherwise never see skill levels, money or ship telemetry. Values that arrive while automatic login owns the session reach scripts already running as ordinary `Events.Msdp` and `Events.Gmcp` events once it ends, and when a private interval ends (a password prompt, the Private toggle) the client asks the world again for every variable a script has read, since the answer that landed in the packet ending the interval was withheld.

Reading `msdp.<VARIABLE>` for a variable the cache does not hold also asks the world once to report it: the client sends an MSDP `REPORT` followed by `SEND` for that name, exactly as it does for mapped variables, and the answer arrives as an ordinary `Events.Msdp` event. A script may ask for up to 64 distinct variables; a name is 1 to 128 letters, digits or underscores and does not start with a digit. Names the world's mapping already reports are not asked for again, and a reconnect asks again.

## Script panels

A script never touches the user interface toolkit. It declares a panel as data, and the client renders it as a dockable tool with native controls and the current theme's brushes.

```js
const ship = mud.panel("ship", { title: "Ship", dock: "right" });
ship.gauge("hull", { label: "Hull", value: 0, max: 100 });
ship.gauge("shield", { label: "Shields", value: 0, max: 100, warn: 0.3 });
ship.label("system", { text: "In orbit" });
ship.button("flee", { label: "Flee", onClick: () => mud.send("flee") });
ship.input("say", { placeholder: "Say...", onSubmit: text => mud.send("say " + text) });

mud.on(Events.Msdp, event => {
    if (event.variable === "SHIPHULL") ship.gauge("hull", { label: "Hull", value: Number(event.value), max: 100 });
});
```

`mud.panel(id, options)` creates the panel the first time and returns the same builder afterwards. `options.title` is the tool title and defaults to the panel id; a later call that omits it keeps the title already set. `options.dock` is `"left"`, `"right"` or `"bars"` and defaults to `"right"`; `"bars"` is described below.

Every widget call is `panel.<kind>(id, properties)`. Calling it again with the same id updates that widget's properties in place; the panel is not rebuilt and unrelated widgets keep their state.

| Widget | Properties | Callback | Rendered as |
| --- | --- | --- | --- |
| `gauge` | `label`, `value` (number, default 0), `max` (number, default 100), `warn` (fraction of `max`) | none | The resource bar used for mapped vitals; at or below `warn` it takes the warning color. |
| `label` | `text` | none | A wrapping text block. |
| `text` | `text` | none | A read-only wrapping text block for multi-line output. |
| `list` | `title`, `items` (up to 500 strings) | `onSelect(item)` | A list box. |
| `table` | `title`, `columns` (up to 32), `rows` (up to 500 rows of up to 32 cells) | none | A grid of text blocks. Numbers and booleans in cells become strings. |
| `button` | `label` | `onClick()` | A button. |
| `toggle` | `label`, `value` (boolean) | `onChange(value)` | A check box. |
| `input` | `placeholder`, `value` | `onSubmit(text)` | A text box; Enter submits and clears it. |
| `separator` | none | none | A separator line. |
| `group` | `title`, `children` (widget ids) | none | A bordered section holding the named widgets, in the order given. |

A widget id named by more than one group belongs to the first group that claims it. `panel.remove(id)` removes a widget and its callback. `panel.show()` and `panel.hide()` show and hide the docked tool; `panel.close()` closes it and forgets the panel. Closing the tool by hand has the same effect as `close()`, and the script may declare the panel again.

### Colors in panels

MSDP hands a script the world's raw strings, and on SMAUG and SWR worlds those still carry the world's own color codes: an opponent name arrives as `&228A Vicious Womprat&D`. Every text a widget shows renders those codes, so the script passes the string through as it is. `label`, `text`, list items, table cells, gauge labels, button and toggle labels and group captions are all colored; the panel title is a plain dock tab, so codes are removed from it.

`&` sets the foreground and `^` the background, each followed by one SMAUG letter or by exactly three digits `000` to `255` naming an xterm 256 color (the Legends of the Jedi extension). The letters map onto the same sixteen palette entries the transcript uses, so a color in a panel matches the same code in the transcript and follows the theme:

| Code | Color | Code | Color |
| --- | --- | --- | --- |
| `&x` | black | `&z` | dark grey |
| `&r` | dark red | `&R` | red |
| `&g` | dark green | `&G` | green |
| `&O` | orange | `&Y` | yellow |
| `&b` | dark blue | `&B` | blue |
| `&p` | purple | `&P` | pink |
| `&c` | dark cyan | `&C` | cyan |
| `&w` | grey | `&W` | white |

`&D` (or `&d`) restores the default colors. `&&` is a literal ampersand and `^^` a literal caret; `&` or `^` followed by anything else is ordinary text, so `50% && rising` shows as `50% & rising`. Real ANSI escape sequences are applied when they are simple color sequences and dropped otherwise, since some worlds send those through MSDP too. Every string starts from the default colors, so a code never leaks from one widget into the next.

`mud.format(value)` turns any value into display text for a widget: `undefined` and `null` give an empty string, a string passes through unchanged with its codes, numbers and booleans give their text, an array gives its items joined with `", "`, and an object gives `key: value` pairs joined with `", "`. Nested objects and arrays are formatted the same way to a depth of 4 and the result is cut at 4096 characters. Use it for MSDP tables such as `ROOMEXITS`, which would otherwise print as `[object Object]`.

### Focusing a panel

`panel.focus()` brings the panel's tab to the front of its dock, showing the panel first if it was hidden. `panel.show({ focus: true })` does the same after `show()`. The client accepts one focus per panel per second and drops the rest, so a script that refreshes its panel on every MSDP event cannot keep stealing the tab the user is reading; call it from the event that matters, such as the start of a fight, not from the refresh. `focus()` does nothing for a `bars` panel.

### Bars

`mud.panel(id, { dock: "bars" })` puts the panel's gauges in the vitals strip under the transcript, after the mapped vitals such as Health and Movement, as the same cards. There is no docked tool. Only `gauge` and `label` are accepted on a bars panel: a label is ignored in the strip, and any other widget kind is a script error (`A bars panel accepts only gauge and label widgets.`) that stops the script. `hide()` removes the panel's bars, `show()` restores them, `close()` removes them for good. Several bars panels append one after another in declaration order, and the strip stays hidden while there is nothing to show.

```js
const vitals = mud.panel("vitals", { dock: "bars" });
mud.on(Events.Msdp, event => {
    if (event.variable === "FORCE") vitals.gauge("force", { label: "&CForce&D", value: Number(event.value), max: 100, warn: 0.2 });
});
```

Callbacks run in the worker under the same rules as a trigger: they are synchronous, they may call `mud.send` and `mud.echo`, and the usual send policy, rate limits and privacy pause apply. Panels belong to one session and disappear when their script stops, when the script is disabled or when the session closes.

Panel and widget ids are 1 to 64 characters of letters, digits, dot, dash or underscore. A script may declare up to 8 panels with up to 64 widgets each and may emit up to 32 panel instructions per event. A property string is limited to 4096 characters, a single panel instruction to 64 KiB and one event's panel output to 256 KiB. Breaking a limit is reported as a script error and stops that script; it never affects the client.

## Supplied script packs

A world listing in the directory may carry a `scripts` array. Each entry has an `id`, `name`, `description`, `source`, a `provenance` of `generated` or `reviewed`, and an integer `version`. Anything else in the array is ignored, and a listing without the field changes nothing.

When you open a world, supplied scripts that are not already in that world's library are added as pack scripts. They are marked **Pack** with their provenance in the Scripts page, their source is read-only, and they start enabled. Use **Duplicate** to make an ordinary hand-written copy you can edit; the copy has no pack marker and no policy.

A pack script starts with a restricted send policy: `mud.send` is refused from triggers, timers, line, GMCP, MSDP and key events, panel toggles, inputs and lists, and from top-level code. It is allowed from an alias and from a panel button click. A refused call raises a script error that names the policy and stops that script until you change the setting. The Scripts page shows **Allow this script to send commands** for each pack script; turning it on lifts the restriction for that script only and is remembered.

Pack scripts are refreshed when the listing's `version` changes: the source and name are replaced while your enable switch and your send choice are kept, so a pack script you disabled stays disabled. Hand-written scripts are never touched by a listing.

Supplied source is still source you should read. Provenance says how it was produced, not that it is safe for your character.


## Aliases, regex triggers and timers

```js
// First matching alias in library order consumes the manual command.
mud.alias(/^lh$/, () => mud.send("look"));
mud.alias(/^greet (.+)$/, match => mud.send("say Hello, " + match[1] + "!"));

// Convenient shorthand for matching complete lines.
mud.trigger(/^Exits: (.+)$/, match => mud.echo("Routes: " + match[1]));

// Multiple sends make a command macro.
mud.alias(/^supplies$/, () => {
    mud.send("inventory");
    mud.send("equipment");
});

// Seconds, minimum one. No catch-up burst after private input.
mud.every(60, () => mud.echo("A minute has passed."));
```

Aliases and regex triggers accept a JavaScript `RegExp` or regex string. Their callbacks receive a match array, where `match[1]` is the first capture. All matching triggers run; only the first matching alias handles a command. Callbacks are synchronous; returning a Promise is unsupported.

`mud.send(command)` sends a single command through this script's session, bypassing aliases. It does not enter manual history. Newlines and control characters are rejected; call it repeatedly for multiple commands. `mud.echo(text)` writes plain local text to the transcript and this script's output. Local echo never re-enters server-line events.

## Privacy and limits

Scripts receive only new completed server lines, not historical transcript or unterminated prompts. Manual private input bypasses aliases. Saved login credentials are never passed to scripts. Private input invalidates queued events and pending actions. Network data containing private text is conservatively excluded, including echo negotiations within one packet and fragmented GMCP messages.

Each script runs in its own engine inside the session's worker process; a runaway callback stops that script only. One worker process serves every script of a connection: it starts when the first script runs, ends when the connection closes or its world changes, and if it dies every running script is stopped with an error and started again on a fresh process, at most three times in five minutes. Disabling, deleting or disconnecting stops a script and discards its pending actions. A command already on the network cannot be recalled. Failed callbacks discard their pending actions. Per-script errors and output appear when that script is selected. There are no filesystem, network, credential-vault, .NET object, browser, Node.js or npm APIs.

Run scripts you have reviewed. Process isolation and per-callback execution limits reduce accidents; this is not a hardened operating-system sandbox for hostile code. Jint allocation limits apply per execution rather than to the total retained heap.

Limits include 64 scripts per world, a 4 MiB library file, 256 KiB source per script, 256 hooks, 32 send and echo actions per event, 32 panel instructions per event, 8 panels and 64 widgets per panel, one accepted panel focus per second, 1024 events waiting for the session's worker, and a shared maximum of 20 sends per second and 200 per minute. `docs/scripting-reference.json` carries the same API and limits in machine-readable form, and a test keeps it in step with the engine. Every callback is limited to 300 ms, 100000 statements, 64 MiB of allocation and a recursion depth of 64; a worker process that does not answer within two seconds (plus half a second for every further script in the request) is killed and restarted. A debugger, packages, keyboard macro bindings and asynchronous callbacks are not part of this version.

## Implementation

`IWorldScriptLibraryStore` is injected through application DI. The application uses SQLite for saved libraries and retains legacy import support. `WorldScriptLibrary` owns the per-script `SessionScripts` coordinators and the session's `SessionScriptWorker`, the client of the one worker process. `ScriptLibraryViewModel` mediates the session’s Scripts page; views contain presentation code. Five resx languages are included: English, Spanish, French, German and Brazilian Portuguese.

Native preview verification runs on macOS. Windows/Linux native packaging still needs platform-specific verification.

## Shared editing: proposed follow-up

The navigation change does not change persistence: saved source and enabled flags are currently shared by endpoint, while already-open sessions retain their loaded definitions and independent runtime state. Cross-session edits can still overwrite a saved definition; conflict detection is not implemented.

The [shared script revision proposal](proposals/shared-script-revisions.md) separates shared source revisions from session activation and makes updates explicit.
