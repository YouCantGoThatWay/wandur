# Script panel rail (first pass)

Status: branch `feature/script-panel-rail`. Iterate here; drop the branch if the feel is wrong.

## Why

LOTJ (and packs like it) declare many informational panels with `dock: "right"`. In the owner's Wandur database those enabled pack scripts are:

| Pack id | Panel title | dock |
| --- | --- | --- |
| affect-list | Affects | right |
| attribute-readout | Attributes | right |
| botting-status | Botting | right |
| cockpit-readout | Cockpit | right |
| location-readout | Location | right |
| money-status | Money | right |
| opponent-watch | Combat | right |
| skill-levels | Skills | right |

They all came from the Wandur directory plan and live in the local sqlite `scripts` table. With eight right-docked tools competing with Map and Channels on the shell edge, the play surface shrinks and the session feels like a dock farm rather than a mud view.

`dock: "bars"` already solved gauges by joining the vitals strip under the transcript. This proposal does the same kind of move for everything else: keep Map and Channels as shell docks, put script UI inside the session's mud view.

## Proposal

- Non-`bars` panels render in a **session rail** beside the transcript inside `TerminalView`, not as Dock tools.
- The vitals strip and command composer span the full mud view under transcript+rail, so Health / Movement stay put.
- Left and right declarations land in the same rail for the first pass (order preserved; `dock` stays in the API so packs do not break). A later pass may split sides or stack.
- `bars` is unchanged.
- The rail shows only when the current session has at least one visible non-bars panel. Sections are an accordion: titles stay visible, the open body fills leftover height with PanelBrush. A top-bar control folds the rail.
- Panel views stay subscribed while collapsed so MSDP-driven tables (Skills Combat level, etc.) keep updating.
- Follow-up (pack script, not chrome): opponent identity belongs on the mapped opponent vitals card, not a Combat accordion section.
- Workspace `SyncScriptPanels` no longer attaches panel ToolDocks. Map / Channels / world library stay as they are.

## Out of scope for this branch

- Changing pack sources or the directory plan.
- New dock values or deprecating `left` / `right` in the script API.
- Persisting rail width or collapsed state.
- Accordion / multi-panel simultaneous view (tabs only for now).

## Success check

Open LOTJ with the pack scripts enabled: Map and Channels stay on the right edge; Affects / Skills / Combat and the rest appear as tabs in the mud-view rail; vitals / bars gauges stay under the transcript.
