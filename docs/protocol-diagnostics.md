# Protocol diagnostics

Each world session has **Play** and **Diagnostics** tabs in a compact footer at the bottom of the session. Play is selected initially. Switching views preserves the transcript, scroll position, command draft and connection; the command input is shown only in Play, leaving Diagnostics the full content area.

Diagnostics shows incoming GMCP and MSDP messages in arrival order, with local timestamps and package or variable names. Select a message to inspect and copy its contents. GMCP JSON is indented; MSDP tables and arrays are presented as JSON for readability. This includes messages the mapper does not understand, such as character vitals and standalone MSDP variables. Malformed messages remain inspectable as escaped text (GMCP) or hexadecimal bytes (MSDP).

**Follow latest** selects new messages automatically. Selecting an older message pauses following. **Clear** removes diagnostic history and the observed field inventory. The list and details pane can be resized using their divider.

History belongs to that session and stays in memory: up to 200 messages and 1,048,576 payload characters. The transport discards subnegotiation frames exceeding its 16 KiB buffer, which includes the option byte. Accepted messages are limited to 32,768 displayed characters, with a truncation notice if formatting exceeds that limit. Reconnecting clears the previous connection's history; disconnecting leaves it available for inspection. Capture starts with the new connection and cannot recover messages received by an older build.

Incoming structured data remains visible during private input and automatic login. Diagnostics makes a separate redacted copy before queuing it: known password, token, credential and authentication-header fields are masked recursively. Login offers and results remain readable. Credential, token and other sensitive `Char.Login.*` packages retain their package name but mask the body. Unparseable or truncated private payloads are masked because their fields cannot be safely separated.

Up to eight saved-login passwords or manually submitted private inputs are remembered only for the current connection to redact exact echoes inside response text; that list is cleared when the session is disposed. This covers known secrets and recognized sensitive fields, not every possible game-specific secret convention. Diagnostic payloads stay in memory. Outgoing commands and credentials are not captured, and nothing is automatically exported or uploaded.

Diagnostic visibility does not relax automation privacy. Raw login/private traffic remains blocked from scripts and the agent by the original privacy and queue-epoch checks, including private intervals within one network read. Previously hidden entries cannot be reconstructed; reconnect with the updated client to capture the login exchange.

An empty history means no GMCP/MSDP messages have been received by this connection yet. The map's protocol status still distinguishes negotiation support from actual room fields received; protocol support alone does not guarantee that a server sends room IDs or any particular package.

## Discovery and observed fields

On GMCP negotiation the client advertises Room, Char, Char.Skills, Char.Items, Char.Afflictions, Char.Defences, Group, Comm, Comm.Channel and MSDP, all at version 1. Char.Login version 1 is also supported for saved password login. There is no universal GMCP wildcard or complete module registry. Unknown packages still appear in Diagnostics if the server sends them. Advertising a data module does not mean Wandur already renders every field in its gameplay UI. Media, web views and other interactive capabilities are not advertised.

For MSDP, the client requests `LIST REPORTABLE_VARIABLES` and subscribes to every valid advertised name, including game-specific fields. It also performs this discovery through the GMCP `MSDP` package. Reports are deduplicated separately for each transport, sent in batches of 32 and limited to 256 subscriptions per transport per negotiation. Names must be ASCII alphanumeric or underscore, cannot start with a digit and are limited to 128 characters. Re-negotiating a disabled protocol resets its subscriptions. Receipt of more fields is still bounded by the transport and diagnostics limits above.

**Observed fields** shows a values-free JSON inventory and a SHA-256 fingerprint. It survives message-history eviction until Clear or reconnect. Fields are accumulated across partial updates. Property ordering, scalar values and array length do not change the fingerprint; new fields or types do. It is an observed subset, not proof of the server's full schema. Limits are 2,048 paths, 512 characters per path and 16 levels of traversal; hitting an inventory limit sets `limited: true`. Malformed, truncated, redacted and authentication messages are excluded. Wire strings stay strings, including numeric MSDP values.

See [Shared protocol mappings](proposals/protocol-mappings.md) for the planned common format, local overrides and service-assisted mapping. These mapping editors and service jobs are not implemented by the discovery change.
