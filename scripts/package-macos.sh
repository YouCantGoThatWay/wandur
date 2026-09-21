#!/usr/bin/env bash
set -euo pipefail
export AVALONIA_TELEMETRY_OPTOUT=1
# Do not reuse worker nodes left by another build on this machine, and do not leave
# any behind. Shared nodes carry cached project state between unrelated solutions.
export MSBUILDDISABLENODEREUSE=1

usage() {
  cat >&2 <<'USAGE'
usage: package-macos.sh [--clean] [--force]

  --clean  Delete obj/ and bin/ first. Off by default: a cold graph here has
           produced a Wandur.deps.json missing its own project references, which
           builds and packages cleanly and then aborts on launch. The incremental
           path does not do this. Use it only when you suspect stale output, and
           check the result.
  --force  Package even if Wandur is running or Rider has the solution open.
USAGE
  exit 2
}

clean=0
force=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --clean) clean=1 ;;
    --no-clean) clean=0 ;;
    --force) force=1 ;;
    -h|--help) usage ;;
    *) echo "unknown option: $1" >&2; usage ;;
  esac
  shift
done

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Build the macOS app on macOS. Use dotnet publish for other platforms." >&2
  exit 1
fi

project_root="$(cd "$(dirname "$0")/.." && pwd)"
app_bundle="$project_root/artifacts/macos/Wandur.app"

# The bundle is replaced with rsync --delete, which pulls files out from under a
# running process. Quit the app rather than debug the crash that follows.
if [[ $force -eq 0 ]] && pgrep -f "$app_bundle/Contents/MacOS/Wandur" >/dev/null 2>&1; then
  echo "Wandur is running from $app_bundle. Quit it first, or pass --force." >&2
  exit 1
fi

# A run killed before its trap fired leaves its publish directory behind.
rm -rf "$project_root/artifacts/macos/".publish.*

if [[ $clean -eq 1 ]]; then
  # Rider restores into obj/ in the background whenever it notices the project
  # change that clearing obj/ is. Two restores in one directory produce
  # "the file ... already exists", an empty obj/<config>/<tfm>/ref/ that fails the
  # next project with CS0006, or a bin/ missing its deps.json. All three look like
  # build bugs and none of them are, so this stops before starting the fight.
  # Match Rider's backend only. The JetBrains Toolbox daemon is not a builder, and
  # idle MSBuild worker nodes (/nodemode:, kept alive by nodeReuse) linger after every
  # build, so matching those would refuse to run almost always.
  if pgrep -f 'ReSharperHost|JetBrains\.Roslyn\.Worker' >/dev/null 2>&1; then
    if [[ $force -eq 0 ]]; then
      echo "Rider has this solution open and will restore into obj/ while this script" >&2
      echo "clears it. Quit Rider, or re-run with --no-clean to skip the clean." >&2
      exit 1
    fi
    echo "warning: cleaning while Rider is open; output may be corrupt." >&2
  fi
  # Driven off the project files so new projects and the SDK submodule are included,
  # and so directory-server/.venv is never in scope.
  while IFS= read -r project; do
    rm -rf "$(dirname "$project")/obj" "$(dirname "$project")/bin"
  done < <(find "$project_root/src" "$project_root/tests" "$project_root/external" \
             -name '*.csproj' -not -path '*/.venv/*' 2>/dev/null)
fi

# Serial, because after a clean every project creates its obj/ files from nothing and
# projects sharing a dependency have raced to write the same nuget.g.props, failing with
# "the file ... already exists". A no-op when everything is already restored.
if ! dotnet restore "$project_root/Wandur.sln" --disable-parallel; then
  echo "restore failed, retrying once" >&2
  sleep 2
  dotnet restore "$project_root/Wandur.sln" --disable-parallel
fi

mkdir -p "$app_bundle/Contents/MacOS"

# Publish into an empty directory: package timestamps can be older than DLLs
# left by a previous dependency version, which incremental publishing may skip.
publish_dir="$(mktemp -d "$project_root/artifacts/macos/.publish.XXXXXX")"
trap 'rm -rf "$publish_dir"' EXIT

dotnet publish "$project_root/src/Wandur.Desktop/Wandur.Desktop.csproj" \
  -c Release --no-restore --no-self-contained --disable-build-servers \
  -o "$publish_dir"

# A publish that produced no launcher must not reach the bundle: rsync --delete
# would empty a working app and leave nothing to fall back to.
if [[ ! -x "$publish_dir/Wandur" ]]; then
  echo "Publish produced no Wandur executable; leaving the existing bundle alone." >&2
  exit 1
fi

# A publish has emitted a deps.json missing its own project references. The host builds
# its assembly list from that manifest, so Wandur.Core.dll sat in the bundle where nothing
# would ever look at it and the app aborted on launch with FileNotFoundException. Every
# file was present and the bundle looked correct, which is exactly why this is checked:
# a complete set of files is not the same thing as an app that starts.
python3 - "$publish_dir" <<'DEPSCHECK'
import json, pathlib, sys
publish = pathlib.Path(sys.argv[1])
manifest = publish / "Wandur.deps.json"
if not manifest.is_file():
    sys.exit("publish produced no Wandur.deps.json")
deps = json.loads(manifest.read_text())
listed = {pathlib.PurePosixPath(path).name
          for target in deps.get("targets", {}).values()
          for library in target.values()
          for path in (library.get("runtime") or {})}
missing = sorted(dll.name for dll in publish.glob("Wandur*.dll") if dll.name not in listed)
if missing:
    sys.exit("deps.json does not list " + ", ".join(missing)
             + "; the app would abort on launch. Re-run; this is a bad build, not a code error.")
DEPSCHECK

rsync -a --delete "$publish_dir/" "$app_bundle/Contents/MacOS/"

# Remove symbols left by an earlier build of this bundle; Release omits them.
rm -f "$app_bundle/Contents/MacOS/Wandur.pdb" "$app_bundle/Contents/MacOS/Wandur.Core.pdb"

# Bundle icon, generated from the 1024 px master by scripts/make-icons.sh.
bash "$project_root/scripts/make-icons.sh" >/dev/null
mkdir -p "$app_bundle/Contents/Resources"
cp "$project_root/artifacts/icons/Wandur.icns" "$app_bundle/Contents/Resources/Wandur.icns"

cat > "$app_bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Wandur</string>
  <key>CFBundleDisplayName</key><string>Wandur</string>
  <key>CFBundleIdentifier</key><string>net.wandur.client</string>
  <key>CFBundleExecutable</key><string>Wandur</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>Wandur</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST

echo "Built: $app_bundle"
