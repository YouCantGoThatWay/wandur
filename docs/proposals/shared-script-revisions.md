# Proposal: shared scripts with explicit session updates

Status: proposal; not part of the workspace navigation implementation.

## Ownership

- The world library owns saved script definitions and immutable revisions.
- Each open session owns its enabled set, loaded revision, runtime variables, timers, and drafts.
- A saved connection is not an account identity. Multiple open sessions may use the same connection with different manually entered credentials.
- Reusable named script presets can supply defaults, but an open session's changes should not silently alter another session's activation settings.

## Editing and updates

Saving updates the shared definition and reloads the saving session's enabled instance, as today. Other sessions retain their running revision and display **Update available**. Reload is explicit, with an option to adopt updates when next connecting. Changing a draft never modifies another editor.

Save includes the revision the editor started from. If that revision has changed, show the local draft and the new saved source. Offer compare/merge, reload with explicit draft discard, or save as a separate script. Never silently overwrite another session's save.

Deletion removes the library entry for future use, but requires a defined policy for already-running copies. Recommended: mark them removed and let the user stop them or save a new copy; do not unexpectedly remove handlers mid-action.

## Storage and compatibility

Use the existing SQLite database, with separate script revisions and activation presets. Preserve old source and enabled flags during migration, turning existing activation settings into a named default preset. Resolve imported script IDs and world identity before enabling anything automatically.

Current behavior is unchanged: definitions and enabled flags are saved per endpoint; each open session has separate loaded definitions and runtime state. Revision notifications, conflict handling, and independent persisted activation presets are future work.
