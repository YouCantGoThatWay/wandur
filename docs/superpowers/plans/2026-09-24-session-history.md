# Session history implementation plan

> Use test-driven development and a final independent code review. Native
> integration and privacy work stays with the main agent; disjoint storage and
> view work can proceed in parallel under the agreed interfaces below.

**Goal:** Retain and search local MUD interactions without slowing gameplay.
**Architecture:** SQLite session/entry tables plus external-content FTS5;
bounded background recording; paginated read-only history window.
**Tech stack:** Existing .NET 10, Microsoft.Data.Sqlite, Avalonia 12, xUnit.
**Spec:** ../specs/2026-09-24-session-history-design.md

## Global constraints

No embeddings, packages, network services, live data reads, recursive routines,
or live-session restarts. Work on main per user preference. Preserve private
input and normalize stream fragments before persistence. All UI strings in
the five localization resources. Retention default 30, allowed 0/30/90/365.

## Task 1: SQLite persistence and keyword search

- [x] Add failing temporary database tests, then storage and migration to v7.
- [x] Prove FTS words, phrases, hostile literal query input, pagination, filters,
  reopen, deletion including active append races, retention and index consistency.
- [x] Run core focused tests and report exact failures/successes.

Files: Core/History/HistoryStore.cs, Core/Storage/ClientDatabase.cs,
tests/Wandur.Core.Tests/SessionHistoryStoreTests.cs.

Contract (namespace Wandur.Core.History):
`HistorySession(string Id, string WorldKey, string WorldName, string CharacterName,
DateTimeOffset StartedAt, DateTimeOffset? EndedAt = null)`;
`HistoryEntry(long Sequence, DateTimeOffset At, string Kind, string Text)`;
`HistoryHit(HistorySession Session, HistoryEntry Entry)`;
`HistoryFilter(string? World = null, string? Character = null,
DateTimeOffset? From = null, DateTimeOffset? Until = null)`.
`IHistoryStore`: void Append(HistorySession session, IReadOnlyList<HistoryEntry> entries);
IReadOnlyList<HistorySession> Sessions(HistoryFilter filter, int offset=0, int limit=100);
IReadOnlyList<HistoryHit> Search(string query, HistoryFilter filter, int offset=0, int limit=100);
IReadOnlyList<HistoryEntry> Entries(string sessionId, long fromSequence=0, int limit=200);
void Delete(string sessionId); void Prune(DateTimeOffset before).
`SqliteHistoryStore(ClientDatabase database)` implements it; synchronous methods
are called only by background workers/UI Task.Run. Date filters are half-open.
Kinds are received/sent/script/private. Empty Append updates session metadata.

## Task 2: Bounded capture and preferences

- [x] Add failing tests for fragmented ANSI/text/secrets, private boundaries,
  chronological command ordering, stop/restart capture, full queue, storage errors.
- [x] Implement Core/History/HistoryRecorder.cs and controller integration;
  inject IHistoryStore through App/MainWindow/SessionWorkspace.
- [x] Add Saved settings HistoryEnabled=true, HistoryRetentionDays=30 with
  validation and UI controls. No destructive preview; retention executes in worker.
- [x] Test actual loopback output and command capture, retention persistence,
  shutdown drain and history failures leaving gameplay usable.

## Task 3: History browser

- [x] Add failing view/view-model tests for paged sessions, literal search, stale
  async completions, context navigation and deletion confirmation.
- [x] Implement HistoryViewModel and HistoryWindow against IHistoryStore above.
  Add a View menu action in the main agent's integration pass.
- [x] Add new localization keys in all five resources and regenerate facade.
- [x] Capture populated Slate history window and inspect readability/layout.

## Review focus and final checks

- Split private echoes across chunks and ANSI; no secrets on disk or in FTS.
- Multi-session writes/deletion races; no resurrected deleted history.
- Cancelled preference edits; no premature deletion.
- Long output/many matches; bounded memory, cancellable stale view results.
- Storage failure; visible warning without terminating a session.
- [x] Full Release build/tests, localization check, final independent review.
- [x] Document verified behavior and limits; commit exact owned files, no push.
