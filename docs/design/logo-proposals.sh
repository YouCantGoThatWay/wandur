#!/usr/bin/env bash
# Generates logo proposals for Wandur with the configured Azure OpenAI image deployment.
# Source the env file first (set -a; source <env file>; set +a). Never prints the key.
set -euo pipefail
: "${AZURE_OPENAI_ENDPOINT:?}" "${AZURE_OPENAI_API_KEY:?}" "${AZURE_OPENAI_IMAGE_DEPLOYMENT:?}"
out="$(dirname "$0")/logos/${AZURE_OPENAI_IMAGE_DEPLOYMENT}"; mkdir -p "$out"
quality="${LOGO_QUALITY:-high}"
root="${AZURE_OPENAI_ENDPOINT%%/openai*}"; root="${root%/}"
api="${AZURE_OPENAI_API_VERSION:-preview}"

brief='Wandur is a directory and desktop client for MUDs: text-based multiplayer worlds you explore by typing. The name means wander, and the product is a gateway: it finds worlds, shows live data about them (who is online, how they are doing), and opens the door to play. Brand vibe: dark, elegant, quiet confidence; near-black backgrounds with a slight warmth, a single amber accent, monospace terminal heritage, the romance of maps, thresholds and lantern light rather than swords and dragons. The audience is adults who remember text worlds and newcomers curious about them.'

rules='The mark alone, no words, no letters, no text of any kind. A single simple symbol that is memorable at a glance and still readable at 16 pixels as a favicon and as a rounded-square app icon. Show it large on near-black in amber, and small beside it in one-colour black on white. Flat vector style, crisp geometry, at most two colours, no gradients, no 3D, no bevels, no glow, no mockup scenes, no backgrounds beyond the flat fill.'

variants=(
  'Direction A: a doorway or gateway mark, an open threshold with a path leading through it, minimal geometry.'
  'Direction B: a wayfinding mark built from a single continuous line, like a route on a map ending in a point of light.'
  'Direction C: a terminal cursor or bracket shape turned into a symbol of a doorway.'
  'Direction D: a lantern or star at the end of a path, reduced to three or four shapes, warm on dark.'
)

i=0
for v in "${variants[@]}"; do
  i=$((i+1))
  prompt="$brief $rules $v"
  body=$(python3 -c 'import json,sys; print(json.dumps({"model": sys.argv[1], "prompt": sys.argv[2], "n": 1, "size": "1024x1024", "quality": sys.argv[3], "output_format": "png"}))' "$AZURE_OPENAI_IMAGE_DEPLOYMENT" "$prompt" "$quality")
  echo "generating direction $i ..."
  curl -s -m 240 -X POST "$root/openai/v1/images/generations?api-version=$api" \
    -H "api-key: $AZURE_OPENAI_API_KEY" -H "Content-Type: application/json" -d "$body" \
    | python3 -c 'import base64,json,sys; d=json.load(sys.stdin); open(sys.argv[1],"wb").write(base64.b64decode(d["data"][0]["b64_json"]))' "$out/logo-$i.png" \
    && echo "  saved $out/logo-$i.png" || echo "  direction $i failed"
  sleep 31
done
echo "done: $out"
