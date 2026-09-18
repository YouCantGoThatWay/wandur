#!/usr/bin/env bash
set -euo pipefail
export AVALONIA_TELEMETRY_OPTOUT=1

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Build the macOS app on macOS. Use dotnet publish for other platforms." >&2
  exit 1
fi

project_root="$(cd "$(dirname "$0")/.." && pwd)"
app_bundle="$project_root/artifacts/macos/Wandur.app"
mkdir -p "$app_bundle/Contents/MacOS"

# Publish into an empty directory: package timestamps can be older than DLLs
# left by a previous dependency version, which incremental publishing may skip.
publish_dir="$(mktemp -d "$project_root/artifacts/macos/.publish.XXXXXX")"
trap 'rm -rf "$publish_dir"' EXIT

dotnet publish "$project_root/src/Wandur.Desktop/Wandur.Desktop.csproj" \
  -c Release --no-restore --no-self-contained --disable-build-servers \
  -o "$publish_dir"
rsync -a --delete "$publish_dir/" "$app_bundle/Contents/MacOS/"

# Remove symbols left by an earlier build of this bundle; Release omits them.
rm -f "$app_bundle/Contents/MacOS/Wandur.pdb" "$app_bundle/Contents/MacOS/Wandur.Core.pdb"

cat > "$app_bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Wandur</string>
  <key>CFBundleDisplayName</key><string>Wandur</string>
  <key>CFBundleIdentifier</key><string>net.wandur.client</string>
  <key>CFBundleExecutable</key><string>Wandur</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST

echo "Built: $app_bundle"
