# Local searchable session history

The user approved lightweight local SQLite history and keyword search, with
embeddings explicitly deferred. Use the current main checkout as requested
in this conversation. No network services, model downloads, new packages,
live profile inspection or running-session restarts.

Persist readable, plain-text interactions, with timestamps, direction,
world/endpoint, character and a fresh session identifier for every connection.
Index with SQLite FTS5, literal AND words and quoted phrases, and filter by
world/character/date. History is read-only: viewing or searching never sends
commands. Search results open surrounding chronological context. Paginate
both results and transcript, never load the whole database into the UI.

Capture defaults on with a visible notice in the History view and Settings.
Retention defaults to 30 days; offer 90, 365 and forever (zero). Turning capture
off is immediate on Save and does not erase existing history. Retention changes
apply only after Save, not while previewing preferences. Delete a session with
confirmation. Active deleted sessions must not silently recreate themselves.
The first version preserves readable text, not ANSI colors or protocol dumps.

Use the same receipt-time privacy flags and remembered-secret redaction as
diagnostics, conservatively excluding all private/login intervals. Strip ANSI
before indexing. Preserve text split across network chunks, mask secrets split
across chunks, and never queue private raw input for background persistence.
Privacy boundaries discard incomplete public lines if necessary. Known secret
echoes remain redacted after private mode ends. Detection is best-effort, not a
guarantee that server text never contains sensitive material.

Keep capture CPU/memory bounded; use a bounded background queue with batched
transactions, flush on orderly close, and fail closed with a visible notice on
queue overflow/storage failure. History failure never disconnects a MUD.
WAL is already enabled. No retention work or database queries on the UI thread.

Use existing Avalonia 12/.NET 10 patterns and localized strings in all five
languages. User-visible entry is View > Session history, opening a resizable
window so older sessions remain accessible without an active connection.

Verification: real temporary SQLite/FTS migration and reopen tests, privacy
and network-fragment tests, batching/failure/retention tests, loopback capture,
headless UI search/open/delete and preference Save/Cancel tests, visual capture,
localization generator check, full solution tests and independent review.
