# Local model agents

Wandur can ask a model to operate one open MUD connection. Agent settings are shared by connections to the same world; each connection has its own goal selection, recent observations, memory and run state.

## Set up LM Studio

1. Start LM Studio's local server and load a model.
2. Edit a saved world and choose **Agent settings** in the left-hand section list.
3. Enter **Server address**, such as `127.0.0.1:1234`, `my-server:8080`, or `https://models.example.net`. Choose **LM Studio (native API)** or **OpenAI-compatible** under **Provider / API format**. Wandur adds the appropriate API path; the hostname and port are yours to choose.
4. Models load automatically when settings open or the address, provider, or credential changes. A short typing delay avoids requesting every keystroke. A single available model is selected when the model field is empty; otherwise choose one or enter an ID manually. Refresh is an optional retry. Errors distinguish unreachable servers, authentication problems, timeouts and incompatible responses. A listed model may require loading before use.
5. Edit **Instructions**, **Goals**, and **Allowed commands**, then save with the toolbar disk icon or Cmd/Ctrl+S. Credentials are optional for local servers and stored in the OS credential vault when supplied.
6. Connect and log in normally. Open **Agent ▾** in the session footer, select one goal to pursue, then press **Play**. The footer also has a Play/Stop control and the current agent status. **More controls** contains Preview, Step, and Reset memory.

LM Studio's normal running server exposes both formats; no separate OpenAI-compatible startup mode is needed. The provider registry keeps formatting separate from the agent runner, leaving room for future adapters such as Anthropic.

| Provider | Appended API path | Discovery | Inference |
| --- | --- | --- | --- |
| LM Studio (native API) | `/api/v1` | `/models` (chat models only) | `/chat` |
| OpenAI-compatible | `/v1` | `/models` | `/chat/completions` |

The native adapter uses stateless requests (`store: false`) without server-side integrations. Both adapters validate the same fixed action IDs and bound prompt/response sizes. API keys remain bound to the saved full endpoint and provider. Existing saved full URLs are displayed as server addresses without changing their stored configuration until edited.

References: [LM Studio native API](https://lmstudio.ai/docs/developer/rest), [native chat](https://lmstudio.ai/docs/developer/rest/chat), [native model list](https://lmstudio.ai/docs/developer/rest/list), and [OpenAI compatibility](https://lmstudio.ai/docs/developer/openai-compat).

## Live controls and goals

The footer has separate **Scripts**, **Macros**, and **Agent** controls. Scripts lists individual checkboxes. Macros switches this session’s enabled macros on or off without altering their saved individual defaults or other sessions. Agent goals are saved locally per world; each open connection selects one goal with a radio button. Selecting another goal stops the run and clears its working memory. Goals and templates are controlled by the user, not the directory server.

Use **Agent settings → Goals** to add, edit, or delete goals. Each has a name, a Markdown description, and Markdown rules. Both Markdown fields use AvaloniaEdit (the scripting editor engine), with highlighting for headings, lists, emphasis, links, inline code and fenced code, plus undo/redo and word wrapping. The two editor tabs share the available space.

**Add template…** offers Observe surroundings, Explore carefully, and Review inventory. Each adds an independent editable copy with a fresh ID; templates never start automatically or change the command catalog. You can select one default for new sessions. Choose **Save world** at the bottom of the settings dialog before running. Agent settings share the same Save/Cancel draft as the other connection sections. Existing goal text is retained as its description; older profiles with several checked defaults retain all goals but select only the first.

The selected description and rules are sent together as Markdown to the model. Rules are model instructions, not a new deterministic policy engine. The command allowlist and run limits remain enforced by the client. Unknown or unavailable commands must be added explicitly in **Allowed commands**; adding a template does not grant new actions. Name, description and rules together have a 4,000-character prompt limit per goal. Play is disabled without a selected goal.

## Commands

Each row has three fields separated by `|`:

```text
look | look | Examine the current room
north | north | Move north
south | south | Move south
score | score | Check character status
inventory | inventory | List carried items
```

The first field is a unique action ID, the second is the exact MUD command to send, and the third explains its meaning to the model. This initial version uses fixed commands: no model-generated arguments, script execution, chained commands or aliases. Add specific actions such as `buy_bread | buy bread | Buy food` when useful for a world. The starter list contains look and the four cardinal directions. Add up/down, combat, healing or interaction commands explicitly when needed.

**Preview** displays one proposed action and short explanation in **Recent decisions**, without sending it or changing memory. **Step** sends at most one selected command. **Play** repeats within the configured decision and time limits. The model can also return `wait` or `done`. The client waits for fresh public output after a command; this is evidence of a response, not proof that the requested action succeeded. A quiet timeout pauses the run without retrying the command.

**Stop**, manual command input, map walking, login/private input, disconnecting, changing the live goal, or saving agent settings cancels pending work. A decision based on changed observations is discarded. Switching to another connection does not retarget a running agent.

Step and Run stop existing script workers to keep command sources from competing. Scripts remain stopped afterward: toggle them explicitly in Scripts (or switch Macros off and on) to resume. Preview leaves them alone. This avoids replaying top-level script commands when an agent finishes.

## Context, storage and limits

- Recent public server text, recognized GMCP room/vitals messages, and decoded MSDP room messages form the observation. Common OOC/chat prefixes are filtered; this is a small built-in filter rather than a game-specific parser.
- Privacy-tagged input and login/private-interval data are excluded before entering the agent buffer. The client does not add saved passwords or local command input to observations. A configured remote endpoint receives the public game observations; use localhost for local processing.
- The full prompt has a character budget, including instructions/catalog, plus an output token limit. Compact memory is limited to 2,048 characters; recent decisions to 20 entries. These are working notes, not a transcript or persistent character memory.
- The response timeout defaults to 120 seconds for local inference. Adjust it and the output token limit for your model. For the OpenAI-compatible provider, **Request JSON mode** is available when supported by the model/server. Native LM Studio requests rely on the decision instructions and strict JSON validation. Model discovery has a separate maximum ten-second timeout.
- The API key is separate from SQLite and bound to its endpoint/integration. Changing either drops the stored key reference; enter a key explicitly for the new destination.
- Configuration lives in `world_agent_profiles` inside `wandur.db` (schema version 3). Runs never start automatically and are not resumed after restarting the client.

## Current scope

This first implementation uses a fresh request per decision, fixed commands, bounded recent observations and one agent setup per world. It does not yet have parameterized actions, script-supplied observations, shared global model connections, persistent character memory, streaming inference, or an Anthropic adapter. See the [original proposal](proposals/llm-agent-workspace.md) for the broader design.

## Live verification

Both adapters successfully discovered and requested a validated decision from `google/gemma-4-12b` on the local LM Studio server. Using a synthetic vestibule with an observatory to the north, both returned the permitted `north` action. The measured requests took approximately 55 seconds (compatible) and 52 seconds (native); this is a single functional check, not a performance benchmark or a live-MUD run.
