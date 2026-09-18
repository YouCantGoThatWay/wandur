# Wandur JavaScript scripting

User requested scripting and selected JavaScript after comparing it with Python. Follow established DI, MVVM, five-language resources, and cross-platform requirements.

## Scope and architecture

One explicitly started JavaScript script per session, edited in a nonmodal window opened from Session → Scripts. The editor stays associated with its original session. Source persists per normalized host/port/TLS endpoint (demo has its own key), with an explicit Save action. Opening or connecting never executes saved code. Scripts are not shared between live sessions, although saved source can be reused by tabs for the same world.

Jint runs inside a separate invocation of the Wandur executable using --script-worker. The worker owns engine state, aliases, triggers, and timers. A factory injected through App/MainWindow/SessionWorkspace/WorkspaceController constructs each runtime. Request/response JSON on standard input/output carries only event text, elapsed monotonic time, effect lists and errors. The parent applies effects only to the originating, still-current session/run. Killing the process cancels unresponsive requests. No CLR, filesystem, networking, credentials, or controller objects are exposed to JavaScript; this remains an automation facility for reviewed scripts, not a guarantee against hostile code.

The user additionally requested syntax highlighting: use an Avalonia 11-compatible AvaloniaEdit editor with JavaScript colors for light/dark themes, line numbers and indentation. Keep the document, caret, selection and undo history intact when unrelated status/log changes occur.

## Script API

- mud.send(command): send one nonempty command, no CR/LF or control characters, maximum 4096 characters. Bypasses alias matching to avoid recursion.
- mud.echo(text): local plain-text output, never sent to the server or fed back into triggers.
- mud.alias(pattern, callback): RegExp or regex string, first matching alias consumes user command; callback receives match array.
- mud.trigger(pattern, callback): all matching triggers receive match array for completed ANSI-decoded server lines. Unterminated prompts are intentionally excluded.
- mud.every(seconds, callback): repeating timer, minimum 1 second; no catch-up burst after pauses.
- Per-run JavaScript globals hold state. Run creates a fresh engine; Stop/disconnect cancels handlers and timers.

Synchronous callbacks only, no npm/Node/browser APIs. Bound source size, hook counts, action counts, command rate, event queue, logs, request duration and regex execution. Failures stop the run and are visible in the editor. Queued and partially computed actions from failed requests are discarded.

## Integration and privacy

Actions execute on the Avalonia dispatcher, preserve command tracking/local echo, and cannot leak across sessions/reconnects/restarts. Private manual inputs bypass aliases and scripts receive no credential vault access or automatic-login inputs. Input and output hooks/timers pause during private input and automatic login. Pending events/effects are invalidated when entering private mode. Output line assembly handles ANSI codes and network fragmentation without duplicate trigger calls.

## Verification

Engine tests cover aliases, triggers, timer scheduling, state, errors, constraints, and no CLR access. Runtime tests cover worker protocol and stopping a looping worker. Controller tests cover loopback sending, no alias recursion, fragmented ANSI triggers, private input, stop/disconnect, and session isolation. Editor tests cover save/run/stop/status/source retention. Run full solution, localization check, package macOS, visually verify isolated preview. Do not interrupt the user's connected client.
