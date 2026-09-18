# Agent integration implementation plan

User approved implementation on 2026-09-18. Builds on llm-agent-workspace.md and the sectioned connection editor.

## Initial deliverable

One saved agent setup per world, with LM Studio/OpenAI-compatible endpoint/model selection, optional vault-held credential, system instructions, default goal, explicit static command catalog, and run limits. An injected provider interface allows other API formats later. Typed parameterized commands, multiple agent presets, shared global endpoint management, and persisted character memory remain later extensions.

## Work units

- Provider/storage: validated Core contracts and catalog, bounded Chat Completions transport and model discovery, world-keyed SQLite migration. Test malformed responses, whitelist checks, cancellation, and persistence.
- Runtime/gateway: session-scoped Preview/Step/Run/Stop, bounded public observations and memory, freshness checks, manual takeover/private-input/disconnect cancellation, script/map-walk ownership. Test fake-provider and loopback scenarios.
- UI: Agent section in world configuration using MVVM, saved endpoint/model settings, model discovery, prompt/catalog/limits. Live controls, goal and bounded activity in the existing Automation menu. Localize all static UI in five languages.
- Integrate and verify: headless UI tests, complete Core/Desktop tests, local LM Studio model discovery/preview if available (no live-MUD commands during verification), docs and macOS package.

The run never auto-starts. Preview sends no game command. Step sends at most one. Run enforces decision/time limits. Timeout or uncertain delivery stops instead of retrying a command. Working memory and observations remain in memory; model output cannot change configuration or map state.

## Implementation verification

Implemented the initial scope and the follow-up provider/discovery improvements. The final full Release suite passed: 325 Core and 236 Desktop tests. Localization generation check passed; normal/compact Agent settings screenshots were inspected.

The settings UI accepts a server address and provider, adds the API path automatically, and discovers models after a short typing delay or when settings open. Outdated requests are canceled. Connection, authentication, timeout, API-format and HTTP-status errors have localized explanations.

Both OpenAI-compatible and LM Studio native adapters successfully discovered and requested a validated synthetic-room decision from the user's local `google/gemma-4-12b` model. Both selected the expected permitted north action. No live MUD commands were sent during verification. The macOS bundle was rebuilt with these changes.
