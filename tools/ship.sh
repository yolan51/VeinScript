#!/usr/bin/env bash
# Build the shippable Workbench: a folder you can run from, and a Setup.exe that installs it.
#
#   bash tools/ship.sh [version]        # default 1.0.0
#
# Output, all under out/ (gitignored):
#
#   out/standalone/   VeinScript-Workbench.exe, veinc.exe, samples/, stdlib/   — run it in place
#   out/installer/    VeinScript-Workbench-win-Setup.exe                       — double-click, installs
#
# THIS EXISTS BECAUSE THE RECIPE IS NOT OBVIOUS, and every non-obvious part of it is a mistake
# somebody already made:
#
#   * TWO PUBLISHES, NOT ONE. Publishing the Workbench alone emits `veinc.pdb` and
#     `veinc.runtimeconfig.json` and NO `veinc.exe` — absorbing the Vein.Cli ProjectReference into a
#     single file means the CLI stops being emitted at all. So the CLI is published separately and
#     composed in.
#
#   * `samples/` AND `stdlib/` MUST SIT BESIDE THE EXE. `BundleIndex.LocateNamed` walks up from
#     AppContext.BaseDirectory looking for folders of exactly those names. Move them and every
#     `*Vein.Console.Io.@Print` in every program fails to resolve — with the build, the tests and a
#     developer's own copy all staying green, because a dev copy finds the real ones by climbing out
#     of bin/.
#
#   * PER-USER, NOT Program Files. Velopack installs to %LOCALAPPDATA%, which is what keeps the
#     shipped samples WRITABLE. They are the thing a new person is meant to open, edit and run; under
#     Program Files that needs elevation, and the first thing the app is for stops working.
#
# Needs `vpk` once:  dotnet tool install -g vpk
set -euo pipefail

VERSION="${1:-1.0.0}"
RID="${RID:-win-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PUBLISH=(-c Release -r "$RID" --self-contained true
         -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
         -p:DebugType=none --nologo -v q)

echo "── publishing the CLI ──────────────────────────────────────────"
dotnet publish src/Vein.Cli "${PUBLISH[@]}" -o out/cli

echo "── publishing the Workbench ────────────────────────────────────"
dotnet publish src/Vein.Workbench "${PUBLISH[@]}" -o out/standalone

cp out/cli/veinc.exe out/standalone/veinc.exe
echo "standalone: $(du -sh out/standalone | cut -f1) in out/standalone"

# The check that matters, and the one a developer's copy cannot fail: run the shipped CLI from the
# shipped folder against a shipped sample. If stdlib/ did not come along, this is where it shows.
echo "── verifying the shipped layout ────────────────────────────────"
( cd out/standalone && ./veinc.exe run samples/kitdemo/kitdemo.app.vein --ticks 1 </dev/null >/dev/null ) \
  && echo "ok: the shipped veinc resolves stdlib/ and runs a sample"

echo "── building the installer ──────────────────────────────────────"
vpk pack \
  -u VeinScript-Workbench \
  -v "$VERSION" \
  -p out/standalone \
  -e VeinScript-Workbench.exe \
  --packTitle "VeinScript Workbench" \
  --packAuthors "Yohann" \
  -o out/installer

echo
echo "Setup:      out/installer/VeinScript-Workbench-win-Setup.exe"
echo "Run-in-place: out/standalone/VeinScript-Workbench.exe"
