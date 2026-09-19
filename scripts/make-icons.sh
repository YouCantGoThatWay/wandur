#!/usr/bin/env bash
# Generates the platform icon files from the 1024 px master in src/Wandur.Desktop/Assets/icon-1024.png:
#   src/Wandur.Desktop/Assets/Wandur.ico   (Windows executable icon; committed, needed at build time)
#   src/Wandur.Desktop/Assets/icon-256.png (window icon for Avalonia; committed)
#   artifacts/icons/icon-macos-1024.png    (full-bleed derivative; macOS masks it into its own rounded square)
#   artifacts/icons/Wandur.icns            (macOS bundle icon; built on macOS by package-macos.sh)
# Requires: python3 with Pillow for the .ico and the PNG sizes; sips and iconutil (macOS) for the .icns.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
master="$root/src/Wandur.Desktop/Assets/icon-1024.png"
assets="$root/src/Wandur.Desktop/Assets"
[[ -f "$master" ]] || { echo "missing $master" >&2; exit 1; }

mkdir -p "$root/artifacts/icons"
python3 - "$master" "$assets" "$root/artifacts/icons" <<'PY'
import sys
from PIL import Image, ImageDraw
master, assets, out = sys.argv[1], sys.argv[2], sys.argv[3]
art = Image.open(master).convert("RGBA")
# The master leaves a transparent margin around the artwork. Dock icons must fill the canvas edge to edge,
# because macOS applies its own rounded-square mask and treats anything smaller as a non-standard shape
# that it shrinks onto a plain tile. Crop to the artwork and scale it back up to the full canvas.
box = art.getchannel("A").getbbox() or (0, 0, art.width, art.height)
side = max(box[2] - box[0], box[3] - box[1])
cx, cy = (box[0] + box[2]) // 2, (box[1] + box[3]) // 2
square = art.crop((cx - side // 2, cy - side // 2, cx - side // 2 + side, cy - side // 2 + side)).resize((1024, 1024), Image.LANCZOS)
background = Image.new("RGBA", (1024, 1024), (12, 11, 13, 255))
full = Image.alpha_composite(background, square)
full.save(f"{out}/icon-macos-1024.png", optimize=True)
# Windows and the in-app window icon are not masked by the system, so give those rounded transparent corners.
mask = Image.new("L", (1024, 1024), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, 1023, 1023), radius=230, fill=255)
rounded = full.copy(); rounded.putalpha(mask)
rounded.resize((256, 256), Image.LANCZOS).save(f"{assets}/icon-256.png", optimize=True)
sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [rounded.resize((s, s), Image.LANCZOS) for s in sizes]
frames[-1].save(f"{assets}/Wandur.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
print("wrote", f"{assets}/Wandur.ico", f"{assets}/icon-256.png", "and", f"{out}/icon-macos-1024.png")
PY

if [[ "$(uname -s)" == "Darwin" ]]; then
  out="$root/artifacts/icons"
  master="$out/icon-macos-1024.png"
  iconset="$out/Wandur.iconset"
  rm -rf "$iconset"; mkdir -p "$iconset"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$master" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$master" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$out/Wandur.icns"
  rm -rf "$iconset"
  echo "wrote $out/Wandur.icns"
fi
