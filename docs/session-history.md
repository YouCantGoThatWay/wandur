# Session history

Open **View > Session history** to browse previous connections or search saved
interactions. This is an owned, non-modal window, so gameplay can continue while
you read history. Nothing in this window sends a command to a MUD.

In **Settings > General**, use **Save session history on this device** and
**Keep history**. The default is on for 30 days; options are 90 days, one year
and forever. Changes apply when saved, not while previewing or cancelling
Settings. Disabling recording retains existing history and stops new capture.
Enabling it again while connected starts a new history segment. There is no
backfill of interactions from before this feature or while recording was off.

Search accepts words and double-quoted phrases. Words are combined with AND
within an entry; operators such as OR, NOT and wildcard punctuation are treated
as literal input, not query syntax. Results are newest first. World and
character filters accept partial names; the world filter also accepts an
endpoint. Dates mean on/after From and strictly before Before. Search dates
filter interaction timestamps, while the Sessions tab filters session starts.
Selecting a result opens its surrounding transcript with the matching entry
selected. Results and sessions load 50 at a time; transcript pages load 100.

The first version stores readable plain text, timestamps, sent/received/script
direction and world/character metadata. ANSI styling and protocol payloads are
not stored. Incomplete received lines are held until completed, a local command
boundary or orderly disconnection. Terminal output remains independent of
recording. Raw network chunks are never written to the history database.

Private and login intervals become markers, not recorded text. Known credential
echoes are redacted after ANSI decoding, including echoes split across network
chunks and command boundaries. Entering private mode discards an unfinished
line; after private received text, recording resumes after the next complete
public newline. This can omit the first otherwise-public line after login.
Lines reaching the terminal parser's 4096-character limit are omitted rather than risk retaining
a truncated secret. Detection is best-effort: arbitrary server text may still
contain sensitive information. The SQLite file is not encrypted by this feature.

History stays in the existing local `wandur.db`, with dedicated session and
entry tables and an external-content FTS5 index. Schema version 7 is migrated
transactionally. Retention removes expired entries even within long-running
sessions, at startup, after saved retention changes, and periodically as recording
batches arrive. No model, vector index, new service or package is required.

Delete session requires confirmation, removes its entries and search index,
and prevents later queued writes from recreating that session. Deletion is
logical database deletion, not a guarantee of forensic erasure from disk or
backups. Reconnecting begins a new history session.

Recording uses bounded batches off the UI thread. A full queue or storage error
pauses recording and shows a notice; gameplay is not disconnected. An orderly
close drains pending writes, but an abrupt crash can lose buffered recent text.
Forever retention has no automatic size cap, so extended high-volume sessions
can consume substantial space. Deleted space can be reused within SQLite;
the database file does not necessarily shrink immediately.
