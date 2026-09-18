# Macros and world scripts

Each MUD has saved script and macro definitions. Right-click a saved world and choose **Edit**, or use the active connection’s **Scripts → Edit configuration…** menu. The connection settings window has **Connection, Login, Scripts, and Macros** sections. **Session → Scripts…** opens configuration for the active connection’s world.

The window is resizable. Hide the section list with the top-left toolbar button to widen the editor. The script editor keeps its own compact toolbar, optional output, and API help; the code area fills the available height. Switching sections retains drafts and editor undo history while the window stays open.

Configure a new or existing world in any section, then choose **Save world** to save connection, login, scripts, macros, and agent settings together. All edits, including adding, deleting, and enabling rules, stay in a draft until you save. **Cancel**, closing the window, or switching worlds asks before discarding pending changes. Switching sections keeps your draft. Cmd/Ctrl+S saves the entire dialog.

## Live controls

The active session has **Play** and **Diagnostics** footer tabs. Its **Scripts** menu lists hand-written scripts with individual enable switches and runtime status, a refresh icon to reload saved rules, and **Edit configuration…**. The separate **Macros** footer toggle switches enabled macros on or off for this connection without changing their saved defaults.

Live enable switches affect only that open connection and do not change saved startup preferences. Editing or enabling definitions in configuration does not execute code there or change already-open connections. Choose **Reload saved rules** in a connection to replace its loaded definitions and apply saved enable preferences. This restarts its workers; it does not interrupt another connection. New connections load saved definitions and enable preferences normally.

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

The forms store structured definitions alongside scripts in the world's SQLite library. They generate JavaScript internally and use the same isolated workers, command gateway, rate limits, and private-input pause behavior. Macros are listed separately from hand-written scripts. New macros start disabled. The configuration checkbox saves the startup preference; reload or open a new connection to run that saved rule. Switching views leaves enabled macros running; closing the session stops them. Runtime errors appear in the world configuration.

Definitions and saved enable preferences currently follow the same sharing rules as scripts below. Duplicate open connections have separate workers; a saved change does not replace another already-open connection's loaded definitions. Macros and scripts share the 64-entry library limit. Shortcuts do not run in editors or during private input. `Events.Key` is used internally by generated shortcut macros and receives the selected function-key name as `text`.

## Workflow

1. In configuration, select **Scripts**, choose **New**, give the script a name, and edit its source.
2. **Save world** stores all edited rules in this world’s library along with the other connection settings.
3. Switch **Enabled** on to save its startup preference. Configuration never launches a worker.
4. In the active connection’s **Scripts** menu, use the refresh icon (**Reload saved rules**) to adopt your changes.
5. Use the live enable switch to start or stop a loaded rule for just that connection.

New scripts and scripts migrated from the original single-script editor start disabled. Closing the connection stops every worker. A script error stops only its worker; correct and save its definition, then reload, or toggle its live switch off/on to retry. Private input and automatic login pause automation.

Saved definitions are shared by world identity. Saving merges the edited rules into the on-disk library. Already-open connections retain their loaded definitions until explicitly reloaded. Deleting a rule from configuration requires an inline confirmation and removes its saved definition when you choose **Save world**; reload active connections to remove their previously loaded copy.

The editor includes syntax highlighting, light/dark colors, line numbers, indentation, undo/redo, local output and API examples. Type `mud.` or `Events.` for completion suggestions, or press **Ctrl+Space**. **Tab/Enter** accepts and **Escape** cancels. Event callback parameters receive field suggestions (`text` for Line, `package` and `data` for Gmcp). Suggestions show signatures and descriptions. This is focused API completion, not full JavaScript type checking or a language server; dynamic GMCP fields depend on the MUD. **Cmd+S** on macOS or **Ctrl+S** on Windows/Linux saves. JavaScript is embedded; no Python or Node.js installation is required.

## Events

`Events.Line` and `Events.Gmcp` are named JavaScript constants on a frozen global object. They are not a TypeScript enum. The object and its values cannot be replaced or extended. Existing scripts using `"line"` or `"gmcp"` strings continue to work.

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

`mud.on(Events.Line, callback)` receives `{text}`. `mud.on(Events.Gmcp, callback)` receives `{package, data}`; `data` is parsed JSON, or `null` when the message contains only a package name. Malformed GMCP messages are ignored. Line subscribers run in registration order before regex triggers. Each script has its own globals and worker; sending a command does not create another command event or alias invocation.

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

One isolated worker runs each script. Disabling, deleting or disconnecting stops it and discards pending actions. A command already on the network cannot be recalled. Failed callbacks discard their pending actions. Per-script errors and output appear when that script is selected. There are no filesystem, network, credential-vault, .NET object, browser, Node.js or npm APIs.

Run scripts you have reviewed. Worker isolation and execution limits reduce accidents; this is not a hardened operating-system sandbox for hostile code. Jint allocation limits apply per execution rather than to the total retained heap.

Limits include 64 scripts per world, a 4 MiB library file, 256 KiB source per script, 256 hooks, 32 actions per event, 128 queued events per script, and a shared maximum of 20 sends per second and 200 per minute. An unresponsive worker is killed after two seconds. A debugger, packages, keyboard macro bindings and asynchronous callbacks are not part of this version.

## Implementation

`IWorldScriptLibraryStore` is injected through application DI. The application uses SQLite for saved libraries and retains legacy import support. `WorldScriptLibrary` owns the per-script `SessionScripts` coordinators. `ScriptLibraryViewModel` mediates the session’s Scripts page; views contain presentation code. Five resx languages are included: English, Spanish, French, German and Brazilian Portuguese.

Native preview verification runs on macOS. Windows/Linux native packaging still needs platform-specific verification.

## Shared editing: proposed follow-up

The navigation change does not change persistence: saved source and enabled flags are currently shared by endpoint, while already-open sessions retain their loaded definitions and independent runtime state. Cross-session edits can still overwrite a saved definition; conflict detection is not implemented.

The [shared script revision proposal](proposals/shared-script-revisions.md) separates shared source revisions from session activation and makes updates explicit.
