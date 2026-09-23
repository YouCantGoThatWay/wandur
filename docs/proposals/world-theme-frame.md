# World theme frame (directory-driven chrome)

Status: approved direction for local LOTJ spike. Simple themes stay valid; a frame is optional.

## Goal

A directory world theme can stay a palette (Icesus-style) or add a window **frame** (Imperial bezel for LOTJ). Old clients that ignore unknown fields still apply colors. New clients apply the frame when present and understood.

## Compatibility

- Keep `version: 1`.
- Do not require new fields for `IsValid`.
- Unknown properties must never invalidate a theme.
- If `frame` is missing, malformed, or an unknown `kind`, treat as no frame (palette only).

## Wire shape

Existing fields unchanged: `id`, `name`, `variant`, `corner_radius`, `colors`, optional `surface` (`standard` | `metallic`), optional `images.chrome` / `images.shell` (tiled overlays, opacity ≤ 0.35).

New optional object:

```json
"frame": {
  "kind": "bezel",
  "inset": { "left": 28, "top": 36, "right": 28, "bottom": 32 },
  "content_radius": 0,
  "accent": "#3DB8E8",
  "plaque": "WANDUR",
  "assets": {
    "border": {
      "url": "themes/imperial-bezel/border.png",
      "slice": { "left": 48, "top": 56, "right": 48, "bottom": 48 }
    }
  }
}
```

### Rules

| Field | Rule |
| --- | --- |
| `kind` | Only `bezel` in this spike. Anything else → ignore frame. |
| `inset` | Device-independent px, each 8–96, finite. Missing → client default for that kind. |
| `content_radius` | 0–16. Missing → theme `corner_radius`. |
| `accent` | Optional `#RRGGBB` for edge lights / plaque tint. |
| `plaque` | Optional, ≤ 40 chars, no controls. Shown only if the skin supports it. |
| `assets.border.url` | Same URL rules as `images.chrome` (https absolute, or relative with no `..`). Relative URLs resolve against the directory API origin (e.g. `https://api.wandur.net/themes/...`). |
| `assets.border.slice` | Nine-slice margins in px; each 0–256; left+right < image width, top+bottom < image height when known. |

Tiled `images.shell` / `images.chrome` remain for header grain. The **frame** is a nine-slice border around the window content, not a tile over panels.

## Site responsibilities

1. Document the optional `frame` in theme docs / directory schema notes.
2. Host static frame assets under a public path such as `/themes/imperial-bezel/border.png` (and any companion textures).
3. Seed or upsert `world_themes` for LOTJ host(s) (`legendsofthejedi.com` and any local alias used in smoke) with a light Imperial palette **plus** `frame.kind=bezel` pointing at those assets.
4. Keep a second world (or fixture) on a colors-only theme so both paths stay covered.
5. Validation on write (import/seed): accept palette-only themes; when `frame` is present, enforce the rules above and drop/omit a bad frame rather than dropping the whole theme if colors remain valid.
6. No Azure deploy in this spike unless the owner asks. Local DB / compose is enough.

## Client responsibilities

1. Extend `WorldTheme` (+ converter) with optional `Frame` / nested types; invalid frame → null frame, theme still valid.
2. Resolve relative frame URLs against the configured directory base URL when loading bitmaps (same path used for theme images today).
3. When the active session theme has `frame.kind=bezel` and the border bitmap loads, wrap shell content in a nine-slice bezel with the given inset; otherwise behave as today.
4. Personal / preset themes and unthemed worlds: no frame.
5. Tests: parse frame; ignore bad frame; apply bezel when present; no bezel for palette-only; LOTJ local fixture opens with frame when assets are available.
6. Do not commit secrets or `.env`. Do not push. Commit as `YouCantGoThatWay` with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` only if the owner asked to commit (for this spike: commit on feature branches is OK so the parent can merge; do not push).

## LOTJ local spike palette (starting point)

Light interior, cyan accent (adjust for contrast tests):

- variant: `light`
- shell / panel / terminal: near off-white / cool grey / white terminal
- accent: cyan in the `#2AA8D8`–`#3DB8E8` range
- corner_radius: `0` or `2` (squared Imperial plates)
- frame: `bezel` with nine-slice border asset

## Out of scope for this spike

- macOS traffic-light cosplay on the bezel
- Dense stencil microcopy
- Per-world custom plaque typography beyond a short string
- Forcing frames on personal presets
- NuGet packaging of the SDK (theme types live in client `Wandur.Core` today)
