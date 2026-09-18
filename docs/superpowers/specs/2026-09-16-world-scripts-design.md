# World script library and docked editor

User correction: scripts belong to a MUD, are independently enabled, and subscribe to events. Editing belongs in the docked workspace, with enable controls in the session view.

Use a persistent per-endpoint collection of named scripts, each with stable ID, source and enabled flag. Explicitly enabling authorizes automatic activation for future connections to that endpoint. New scripts and migrated legacy scripts are disabled. Save reloads an enabled script; edits alone never change running code. Each script has a separate existing worker/runtime so one error does not stop other scripts. Privacy and bounded queues remain in SessionScripts. An error remains visible and does not retry continuously; toggle or Save retries.

A dockable Scripts document is tied to its originating session. It contains a script list, New, Delete, editable name, enabled switch, Save, the existing highlighted code editor and selected script output/error. Closing the document does not disable scripts. Session close removes its editor and stops all workers. Reset layout closes editors but leaves session scripts running. A Scripts button in the MUD header opens a flyout listing checkboxes and an Edit scripts action.

Events: mud.on("line", callback) delivers {text}; mud.on("gmcp", callback) delivers {package,data}, parsed from GMCP when valid, with null data for an empty payload. Invalid payload is ignored. Existing mud.trigger, mud.alias and mud.every remain available. Scripts see newly completed ANSI-stripped lines, not historical text or private input. Alias matching uses library order, first handled command wins. All enabled scripts receive lines and GMCP; generated commands do not re-enter aliases.

Keep .NET DI, MVVM, Avalonia 11, Jint worker isolation and five localized resx languages. Preserve existing single-source files on migration. Store library metadata/source atomically with bounded sizes and merge per-script writes so another session's save is not lost. Existing connected apps must not be terminated; verification uses a separate offline preview.

## Follow-up: event constants and API completion

Expose immutable JavaScript Events.Line and Events.Gmcp constants. Keep legacy string event names working. Use AvaloniaEdit completion for mud members, Events constants and fields of the immediately preceding event callback parameter. Show localized descriptions and signatures. Trigger after a dot or Ctrl+Space; Tab/Enter accept, Escape cancels. Suppress in strings/comments; close suggestions when changing script or detaching the editor. This is API-focused completion, not a full JavaScript language server/type checker.
