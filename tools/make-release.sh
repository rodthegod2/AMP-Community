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

cat > dist/RELEASE_NOTES.md <<'NOTES'
# AMP Community @VERSION@

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
- **mod.io delivery** (1.1.0): mods the host got from mod.io come straight from mod.io when it still has the host's version
- **Quest (experimental, 1.1.0)**: a PC host looks up Quest versions of its mods on mod.io (Nexus mods by name, asking the host when unsure); Quest players download those from mod.io, install them in-game and restart (see HOW-TO-PLAY.txt, section 8)
- **Join codes + automatic port opening** (1.2.0): "Host on PC" now opens its port on your router by itself by default (UPnP; removed again when you stop hosting) and shows a short code like `K7QX-29MB` that friends (PC or Quest) type into "Join Code / IP". Turn it off with `"autoPortForward": false` in config.json (`hostingSettings`). When hosting over the internet, set a password (`host_password` in config.json): anyone with your code or IP can try to join.

## Install
1. Download `@ZIP@` below (check it against `SHA256SUMS.txt` if you like).
2. Unzip it and copy the `MultiplayerMod` folder into `<Steam>\steamapps\common\Blade & Sorcery\BladeAndSorcery_Data\StreamingAssets\Mods\` (remove any other AMP copy first).
3. Everyone who plays together needs this same build. Read `HOW-TO-PLAY.txt` (in the zip) for hosting and joining.

## Known limitations
- Not yet tested in VR by the maintainers.
- Quest support is experimental and not yet tested on a headset (install path and in-game swap unverified there).
- Mod sync and settings sync require the host to run this build with "Host on PC".
- Automatic port opening needs UPnP on your router; it can't work when your internet provider (or a second router in front of yours) shares your IP. Then the Internet code isn't shown: forward the port by hand or join on the same Wi-Fi.
- Players still on 1.0.0 re-download mod.io mods that have a `modio-thumbnail.png` once, until they take the offered AMP update.

## Credits
- **AMP** by Adammantium, Apache-2.0: https://github.com/AdammantiumMultiplayer/AMP
- **Netamite** by Adammantium (a modified copy is included; credited, no license file is published upstream)
- Upstream contributors, including misterbubb, LJPCODESTUDIO and others in the git history
NOTES
sed -i -e "s/@VERSION@/$VERSION/g" -e "s/@ZIP@/$ZIP/g" dist/RELEASE_NOTES.md

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
