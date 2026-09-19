#!/usr/bin/env bash
# Generates the platform icon files from the 1024 px master in src/Wandur.Desktop/Assets/icon-1024.png:
#   src/Wandur.Desktop/Assets/Wandur.ico   (Windows executable icon; committed, needed at build time)
#   src/Wandur.Desktop/Assets/icon-256.png (window icon for Avalonia; committed)
#   artifacts/icons/Wandur.icns            (macOS bundle icon; built on macOS by package-macos.sh)
# Requires: python3 with Pillow for the .ico and the PNG sizes; sips and iconutil (macOS) for the .icns.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
master="$root/src/Wandur.Desktop/Assets/icon-1024.png"
assets="$root/src/Wandur.Desktop/Assets"
[[ -f "$master" ]] || { echo "missing $master" >&2; exit 1; }

python3 - "$master" "$assets" <<'PY'
import sys
from PIL import Image
master, assets = sys.argv[1], sys.argv[2]
im = Image.open(master).convert("RGBA")
im.resize((256, 256), Image.LANCZOS).save(f"{assets}/icon-256.png", optimize=True)
sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [im.resize((s, s), Image.LANCZOS) for s in sizes]
frames[-1].save(f"{assets}/Wandur.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
print("wrote", f"{assets}/Wandur.ico", "and", f"{assets}/icon-256.png")
PY

if [[ "$(uname -s)" == "Darwin" ]]; then
  out="$root/artifacts/icons"
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
