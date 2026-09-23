# Wandur directory schema

The server's `/directory` response, server file cache, and desktop cache all use
Wandur's own versioned contract. Python's `schema.py` maps MUDVerse during import;
the desktop's `MudVerseMapper` remains a compatibility path for old caches.
The browser, search, connection profiles, and desktop cache consume `WorldListing`.
Shared fixtures test that both mappers produce the same contract.

```json
{
  "format": "wandur.directory",
  "schema_version": 2,
  "fetched_at": "2026-09-15T20:00:00Z",
  "worlds": [
    {
      "id": "mudverse:98",
      "name": "Aardwolf",
      "summary": "A fantasy world to explore.",
      "description": "Full plain-text description, with paragraph breaks.",
      "host": "aardmud.org",
      "port": 4000,
      "tls_port": null,
      "web_only": false,
      "source": {
        "provider": "mudverse",
        "name": "MUDVerse",
        "record_id": "98",
        "listing_url": "https://www.mudverse.com/game/98",
        "updated_at": null,
        "listed_at": null
      },
      "availability": {
        "online": true,
        "archived": false,
        "archive_reason": "",
        "checked_at": "2026-09-15T20:00:00Z",
        "last_online_at": "2026-09-15T20:00:00Z"
      },
      "population": {
        "latest_count": null,
        "observed_at": null,
        "average_count": null,
        "reported_range": "75-100"
      },
      "features": {
        "theme": "Fantasy",
        "kind": "MUD",
        "language": "English",
        "location": "USA",
        "codebase": "Custom",
        "roleplaying": "Mildly Enforced",
        "player_killing": "Restricted",
        "world_size": "10000+",
        "development_status": "Operational"
      },
      "tags": ["D&D", "dragon"],
      "website_url": "http://aardwolf.com",
      "discord_url": "",
      "play_url": "",
      "banner_url": "",
      "generated_artwork_path": "games/98/art"
    }
  ]
}
```

The example illustrates the contract; it is not a live status report.

## Meaning and mapping

- Identity is a namespaced string (`mudverse:98`), independent of connection
  address. Source IDs and attribution are retained separately.
- Name, summary (`intro`), and full description are decoded to plain text.
  Paragraphs are preserved; HTML markup and legacy escaped apostrophes are cleaned.
- Hostnames are trimmed and lowercased. Invalid ports become null. Plain and TLS
  ports stay distinct; a TLS-only entry creates a TLS profile automatically.
- `availability.online` preserves MUDVerse's nullable `confirmed_online` report.
  False means not confirmed online, not a live offline determination. The latest
  check and last successful connection keep their original timestamps.
- `population.latest_count` is the latest MSSP player count; `observed_at` maps
  `mssp_collected_at`. Zero is a valid observation, and missing is null.
- `population.reported_range` maps the listing's `play_count` category. It is a
  supplied range, not a measured average. MUDVerse's downloaded game records do
  not contain a numerical average, so `average_count` stays null. The Wandur
  contract can carry a measured average when a source actually supplies one;
  the client does not invent a midpoint or fetch history to estimate it.
- Gameplay categories map to named `features` fields; custom tags stay a list.
  Search includes these fields along with names, summaries, descriptions, and hosts.
- Optional `established_at` maps the provider's game creation date, independently
  of the listing's creation/update dates. Optional `community` carries nullable
  `rating` (0–5), `rating_count`, `review_count`, `rank` (positive), and
  `monthly_votes` (nonnegative). These measurements belong to `source`; they are
  not combined global rankings. Absent fields remain unknown and old version-2
  snapshots remain compatible. Ratings require a positive rating count to appear
  or satisfy the rating filter. Review counts and rating counts remain separate.
- Website, Discord, browser-play, source listing, and artwork links accept only
  HTTP(S) URLs without embedded credentials. The shared MUDVerse banner placeholder
  is normalized to an empty banner. Real supplied art takes priority over generation.
- `generated_artwork_path` resolves within the configured Wandur directory service.
  API keys, upstream API URLs, watcher counts, and full review payloads
  are not copied into the client schema.
- `source.updated_at` is when the listing was updated, not when its status was
  checked or when Wandur fetched the directory.

## Compatibility and cache behavior

The FastAPI service imports, stores, and serves version 2. On startup it converts
an existing version-1 raw snapshot locally, saves the original as
`directory.mudverse-v1.json`, and preserves file age for the 24-hour TTL. Generated
artwork is carried forward when normalization changes the text's representation.
No additional provider download or image generation is required for migration.
The desktop accepts version 2 directly and retains its version-1 compatibility path.

An existing version-1 desktop cache migrates locally on load without a network
request and without changing `fetched_at`. If the cache is read-only, migration
still works in memory. The 24-hour refresh policy remains unchanged. Writes are
atomic, and invalid/unsupported snapshots or duplicate IDs cannot replace a usable
cache. Cached banners retain their existing identity across this migration.

## Optional protocol mappings

The discovery worker writes `protocol-mappings.json` beside `directory.json` in
the server's configured cache directory. The sidecar has its own schema version:

```json
{
  "schema_version": 1,
  "worlds": [
    {
      "schema_version": 1,
      "world_id": "mudverse:98",
      "endpoint": {"host": "aardmud.org", "port": 4000, "use_tls": false},
      "schema_fingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "revision": 1,
      "generated_at": "2026-09-18T12:00:00Z",
      "provenance": "deterministic",
      "provisional": true,
      "bindings": [
        {
          "source": {"protocol": "MSDP", "package": "MSDP", "path": "/HEALTH"},
          "target": {"entity": "character", "category": "resource", "key": "health", "member": "current"},
          "label": "Health",
          "conversion": "number",
          "scale": 1
        }
      ]
    }
  ]
}
```

This example is illustrative; its fingerprint does not identify a measured
schema. Each valid document appears as `world.protocol_mapping` on `/directory`
responses. The listing ID and normalized host (trimmed, lowercase, no trailing
dot) must match exactly. The endpoint port must equal `port` when `use_tls` is
false or `tls_port` when true. Matching never falls back to a name, suffix or
different transport. Versions are integer 1, revisions are positive 32-bit
integers, fingerprints are 64 lowercase hexadecimal characters, and
`generated_at` is an ISO timestamp with a timezone. `provenance` is `deterministic`
or `azure`. The optional boolean `provisional` defaults to true: structural
validation does not establish gameplay meaning or complete protocol coverage.

Each mapping contains at most 256 bindings. Sources use protocol `MSDP` or
`GMCP`, a package of 1–128 ASCII letters, digits, underscores or dots, and a JSON
Pointer of at most 512 characters. The empty path addresses the package root;
other paths start with `/` and use `~0` for a literal tilde and `~1` for a literal
slash. Paths are literal references and never expand wildcards. Targets use
entity `character`, `opponent`, `vehicle` or `world`; keys are extensible lowercase
slugs matching `[a-z][a-z0-9_-]*`, at most 64 characters. Category determines the
allowed members and conversion:

| Category | Members | Conversion |
| --- | --- | --- |
| `identity` | `value` | `text` |
| `resource` | `current`, `maximum` | `number` |
| `progression` | `current`, `maximum` | `number` |
| `attribute` | `current`, `base` | `number` |
| `currency` | `carried`, `bank`, `total` | `number` |
| `metric` | `value` | `number`, `text`, `boolean` |
| `location` | `value` | `text` |

Labels are nonblank text up to 80 characters. Text fields reject control
characters and invalid Unicode surrogates. `scale` must be a finite JSON number
greater than zero and at most 1,000,000; text and boolean conversions require
scale 1. Booleans do not count as numbers. Each target may appear only once in a
mapping. MSDP sources require package `MSDP`. Source packages and paths containing
`password`, `passwd`, `secret`, `token`, `credential`, `login`, `auth`, `chat` or
`channel` (case insensitive) are rejected. Required fields must be present. Unknown fields
are stripped at every mapping level, so worker extensions require an explicit
server contract update before they are exposed.

The sidecar is bounded to 16 MiB and read on each API request. A missing, unreadable,
corrupt or unsupported file leaves the catalog usable. Invalid documents are
ignored individually; one malformed binding rejects its entire document.
Duplicate world IDs suppress the ambiguous mapping. The worker should replace
the sidecar atomically. Updates appear on the next request without extending the
directory TTL or changing `fetched_at`, provider fields or artwork identities.
The overlay is never written into the server's `directory.json`; any mapping
embedded there is ignored in favor of the validated sidecar. A desktop may retain
the optional mapping with its own downloaded directory snapshot.

## Optional world appearance

A listing may include `theme`, independently of the gameplay category
`features.theme`. The directory remains schema version 2. Its nested theme contract
has version 1. The palette is required; `surface` and `images` are optional:

```json
{
  "theme": {
    "version": 1,
    "id": "lotj-brushed-alloy-v3",
    "name": "Legends of the Jedi",
    "variant": "dark",
    "corner_radius": 3,
    "surface": "metallic",
    "images": {
      "chrome": {
        "url": "theme-assets/brushed-gunmetal-v1.png",
        "opacity": 0.24
      }
    },
    "colors": {
      "shell": "#18191B",
      "panel": "#25282B",
      "terminal": "#0D0F12",
      "text": "#E7E4DD",
      "muted": "#A7A6A2",
      "accent": "#D3A65F",
      "accent_secondary": "#C9A76B",
      "border": "#373A3E",
      "terminal_text": "#DDDAD3"
    }
  }
}
```

`id` contains 1–80 ASCII letters, digits, dots, underscores or hyphens.
`name` contains 1–100 characters and no control characters. `version` is the integer 1; `variant` is
`dark` or `light`. `corner_radius` is a finite number from 0 through 16 inclusive.
Every listed color is required and uses opaque `#RRGGBB` hexadecimal notation
(case insensitive). Booleans are not numbers. Unsupported versions and malformed
optional themes are ignored without discarding the listing. A missing theme means
the client's default appearance. Themes carry only inert appearance data: no
markup, scripts, or fonts are loaded from them.

`surface` defaults to `standard`; `metallic` adds a restrained gunmetal finish to
titlebars, toolbars and dock headers. Panels, buttons and the transcript keep the
palette's existing appearance. Optional `images.chrome` overlays these header
surfaces; `images.shell` can separately decorate the outer shell. LOTJ uses only
`chrome`. Each image contains `url` and an optional `opacity` (default 0.12,
maximum 0.35). Invalid image entries are ignored without losing the palette.

Optional `frame` adds window chrome around the shell. Only `kind` `bezel` is
understood today: a nine-slice `assets.border` PNG plus `inset` margins (8–96 px
each, or a client default when omitted), optional `content_radius` (0–16, else the
theme `corner_radius`), optional `accent` (`#RRGGBB`) and optional `plaque` (at
most 40 characters). Border `url` follows the same absolute-HTTPS or relative
rules as `images.chrome`; `slice` holds nine-slice margins (0–256 px each).
Relative frame URLs resolve against the directory API origin (for example
`https://api.wandur.net/themes/imperial-bezel/border.png`). A missing, malformed
or unknown `frame` is ignored; the palette still applies. Personal presets never
carry a frame.

Use versioned HTTPS URLs or paths relative to the directory service, not embedded
base64. The desktop caches images by resolved URL in SQLite and reuses them
offline. A new filename refreshes the material without repeatedly downloading it.
This avoids base64's roughly 33% size overhead on directory responses; a portable
self-contained theme export could bundle assets later. Downloads accept PNG only,
at most 4 MiB, with dimensions at most 4096 per side and 8,388,608 pixels total.
The desktop decodes a reduced image for display. Missing images fall back to the
palette/material. Images never overlay transcript text, and switching worlds
cancels the previous theme's pending image load.

The service supplies curated themes for the exact normalized hostnames
`legendsofthejedi.com` and `play.icesus.org` (trimmed, lowercase, without a trailing dot).
The Icesus palette is stored in `directory-server/themes/icesus.json`. Its frozen
night colors and four-pixel corners are sampled from the public
[Icesus web client](https://play.icesus.org/) on September 18, 2026: navy frames,
slate panels, an ink-blue terminal, icy text and pale blue controls. It uses the
standard surface and no external textures. This styles Wandur's existing layout;
it does not add the web client's logo, landscape, character panels or vital bars.
Names, subdomains, and suffix matches do not select a theme. Local enrichment
runs on both existing version-2 caches and fresh provider imports. It preserves
all unrelated fields, `fetched_at`, and the file modification timestamp used for
the 24-hour TTL. It neither downloads provider data nor generates images; artwork
fingerprints remain unchanged. A read-only cache can still be enriched in memory.
Normal expiration and background artwork policies continue to apply independently.

### Desktop behavior

Selecting a world in the directory, saved-world list or toolbar previews its
theme across the entire application: titlebar/toolbars, dock headers, panels,
dialogs, terminal, map canvas and script editor. Switching session tabs applies
that session's theme. Selecting an unthemed world restores the user's saved
default; world previews never overwrite that preference. Closing a directory
dialog restores the selection beneath it, and background directory/status updates
do not take precedence over the user's last selection. The theme travels with the
saved profile in SQLite for offline use. Existing profiles pick up directory changes by exact
host, port and TLS matching on their next connection. A listing without a theme
leaves a saved theme untouched, as a missing protocol mapping does; editing only a
profile's name keeps its theme, and changing its endpoint clears it.

Explicit terminal foreground/background preferences take priority. Server ANSI
colors and semantic map terrain colors retain their meaning. Optional theme
caching failures show a notice but do not prevent an existing world from opening.
The current prototype automatically uses supplied themes; a preference to opt in
or out is deferred. Refresh the client directory and reconnect to adopt a changed
connected session's theme; directory and saved-world previews use the latest
cached listing immediately. Restart an already-running directory service to load the new enrichment
code; the local cached directory already includes the LOTJ prototype.
