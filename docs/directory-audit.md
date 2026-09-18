# Directory coverage and unused data

Read-only audit, September 16, 2026. Baseline: Wandur's existing 240-world
`directory-server/cache/directory.json` snapshot, with its preserved raw
MUDVerse backup for field inspection. No application behavior changed.

## Useful fields

The importer currently omits upstream rating average/count, review count,
monthly votes/rank, creation date, watcher count and MSSP port. In the downloaded
240 records, 62 have ratings, 60 have reviews, 238 have a creation date, and 41
have watchers. Review count and rating count are separate measurements.

Already mapped: theme, game kind, language, location, codebase, roleplaying,
player killing, world size, development stage, custom tags, connection/TLS,
status observation dates and population. 131 records have an observed player
count; six specify a TLS port. These can support structured filters now.

MUDVerse's separate `/games/{id}/players` endpoint supplies daily averages or raw
samples. The importer does not fetch this history. Population charts and measured
period averages need an additional cached import, with date range and sample
coverage recorded. Listed population ranges are not measured averages.

Suggested presentation: artwork/header, compact activity and rating summaries,
Overview / Activity / Details sections, formatted paragraphs and lists, gameplay
badges, connection facts, and source/freshness information. Advanced filters can
combine gameplay, language, population, development stage and TLS; unknown values
must remain distinguishable from zero/false. Ratings and votes stay source-specific.

## Coverage comparison

### MudConnector

Source: https://www.mudconnect.com/cgi-bin/search.cgi?mode=mobile_biglist

- Downloaded and parsed 689 listing rows.
- 105 rows match a cached connection endpoint (host plus plain or TLS port).
- 144 rows match by endpoint, normalized name, or identical hostname.
- 545 rows have none of those matches and are potential additional listings.
- Examples: 3-Kingdoms, 3Scapes, 4 Dimensions, AVATAR Mud, Abandoned Codex.

Names were lowercased and stripped of non-alphanumeric characters for comparison;
hosts were lowercased and stripped of a trailing dot. Host-only matches were
counted conservatively as potential overlap, even when ports differ. These are
listing comparisons, not proof of unique currently reachable games: renamed
games, alternate domains, shared hosts, duplicates and stale entries require
resolution. No MUD connections or login attempts were made.

### Grapevine

Source: https://grapevine.haus/games

- The live default online listing returned 145 entries across six pages.
- 59 names match the cache after normalization; 86 do not. This is only a name
  comparison, not a unique-game estimate (e.g. Aarchon MUD versus Aarchon).
- Verified 4Dimensions' detail page reports `4dimensions.org:6000`, absent from
  our cache by name and host: https://grapevine.haus/games/4D
- Counts vary by filter and time; the web search snapshot showed 149 whereas the
  direct live download showed 145.

### MUDStats

Source: https://mudstats.com/

- Live homepage reported 740 active MUDs. This is the site's reported count, not
  an independently validated total or a count of additional games.
- Verified Narnia MUCK's listing and `muck.narniamuck.org:2050` are absent from
  our cache: https://mudstats.com/World/NarniaMUCK
- The page exposes population statistics, observation timing and other source
  links. A full catalog overlap comparison was not performed.

## Import direction

Keep one Wandur catalog with source adapters. Preserve each source ID/link and
observation date, allow multiple endpoints and aliases per game, and retain field
provenance when sources disagree. Endpoint matching is stronger evidence than
fuzzy name matching; shared hosting and aliases still need care. Check supported
bulk access and reuse terms before adding scheduled imports. MUDVerse has a
documented directory API; this audit did not establish supported bulk APIs for
Grapevine, MUDStats or MudConnector.

MUDVerse API reference: https://www.mudverse.com/api/docs
