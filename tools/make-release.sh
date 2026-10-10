#!/usr/bin/env bash
# Build and package a release: tools/make-release.sh <version> [--publish]
# Without --publish only stages dist/ and prints what would be uploaded.
set -euo pipefail

VERSION="${1:-}"
PUBLISH=false
[ "${2:-}" = "--publish" ] && PUBLISH=true
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || { echo "usage: $0 <version, e.g. 1.0.0> [--publish]" >&2; exit 2; }

REPO=rodthegod2/AMP-Community
BUILD_DIR=/c/Projects/bns-mp/build
cd "$(dirname "$0")/.."

[ -z "$(git status --porcelain)" ] || { echo "Working tree is dirty; commit or stash first." >&2; exit 1; }
grep -q "COMMUNITY_VERSION = \"$VERSION\"" Data/Defines.cs \
  || { echo "Data/Defines.cs COMMUNITY_VERSION is not $VERSION." >&2; exit 1; }

bash /c/Projects/bns-mp/build.sh

ZIP="AMP-Community-$VERSION.zip"
rm -rf dist
mkdir -p dist/MultiplayerMod
for f in AMP.dll AMP.pdb Netamite*.dll Newtonsoft.Json.dll; do
  cp "$BUILD_DIR"/$f dist/MultiplayerMod/
done
[ -f dist/MultiplayerMod/AMP.dll ] && ls dist/MultiplayerMod/Netamite*.dll >/dev/null
cp docs/HOW-TO-PLAY.txt dist/HOW-TO-PLAY.txt

# manifest: ModVersion must equal the release version exactly (the in-game updater checks it)
python - "$VERSION" <<'PY'
import json, sys
m = json.load(open("BS Files/manifest.json", encoding="utf-8-sig"))
m["ModVersion"] = sys.argv[1]
m["Description"] = "Community fork of AMP by Adammantium — stability fixes, host on PC, mod sync."
with open("dist/MultiplayerMod/manifest.json", "w", encoding="utf-8", newline="\n") as f:
    json.dump(m, f, indent=4, ensure_ascii=False)
    f.write("\n")
PY

# zip with forward-slash entry names (Windows PowerShell 5.1 Compress-Archive writes backslashes)
python - "$ZIP" <<'PY'
import os, sys, zipfile
with zipfile.ZipFile("dist/" + sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk("dist/MultiplayerMod"):
        for n in sorted(files):
            p = os.path.join(root, n)
            z.write(p, os.path.relpath(p, "dist").replace(os.sep, "/"))
    z.write("dist/HOW-TO-PLAY.txt", "HOW-TO-PLAY.txt")
PY

(cd dist && sha256sum "$ZIP" > SHA256SUMS.txt)

cat > dist/RELEASE_NOTES.md <<NOTES
# AMP Community $VERSION

A community fork of AMP (Adammantium Multiplayer Mod) for Blade & Sorcery.

## What's new
- Stability fixes across networking, item/spell sync and joining
- **Host on PC**: host a server straight from the game (book > Mods > AMP > Host)
- **Mod check on join**: the host's mod list is compared with yours and the differences are shown
- **Download & restart**: fetch the host's mods and restart, choose Temporary or Keep, with safe restore of your own mods
- Separate multiplayer save while the host's mods are installed, so your characters stay safe
- **Live settings sync**: gameplay Mod Options follow the host's values while connected
- **AMP self-update**: AMP is brought in line with the host's build the same way
- **In-game updater**: AMP checks this releases page at startup; press "Update AMP" in the book when a newer version exists

## Install
1. Download \`$ZIP\` below (check it against \`SHA256SUMS.txt\` if you like).
2. Unzip it and copy the \`MultiplayerMod\` folder into \`<Steam>\steamapps\common\Blade & Sorcery\BladeAndSorcery_Data\StreamingAssets\Mods\\` (remove any other AMP copy first).
3. Everyone who plays together needs this same build. Read \`HOW-TO-PLAY.txt\` (in the zip) for hosting and joining.

## Known limitations
- Not yet tested in VR by the maintainers.
- Quest is not supported yet (coming in a later version).
- Mod sync and settings sync require the host to run this build with "Host on PC".

## Credits
- **AMP** by Adammantium, Apache-2.0: https://github.com/AdammantiumMultiplayer/AMP
- **Netamite** by Adammantium (a modified copy is included; credited, no license file is published upstream)
- Upstream contributors, including misterbubb, LJPCODESTUDIO and others in the git history
NOTES

echo
echo "Staged in dist/:"
(cd dist && ls -l "$ZIP" SHA256SUMS.txt RELEASE_NOTES.md HOW-TO-PLAY.txt && cat SHA256SUMS.txt)

if $PUBLISH; then
  gh release create "v$VERSION" "dist/$ZIP" dist/SHA256SUMS.txt --repo "$REPO" \
    --title "AMP Community $VERSION" --notes-file dist/RELEASE_NOTES.md --target community
else
  echo
  echo "Dry run. Would run: gh release create v$VERSION dist/$ZIP dist/SHA256SUMS.txt --repo $REPO --title \"AMP Community $VERSION\" --notes-file dist/RELEASE_NOTES.md --target community"
fi
