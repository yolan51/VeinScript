#!/usr/bin/env bash
# pack.sh — publish VeinScript as packages an ENGINE project can consume (docs/HOSTING.md).
#
#   bash tools/pack.sh              # 0.1.0-dev.<timestamp> into the local feed
#   bash tools/pack.sh 0.2.0        # an explicit version
#
# The boundary this exists for: a game (e.g. a Godot project) references `Vein.Hosting` BY VERSION from a
# local feed, never by a path into this repo, so the two solutions never share a build. Upgrading the game
# is bumping a number.
#
# A fresh timestamped pre-release every run, because NuGet caches a package by version forever: re-packing
# 0.1.0 would leave every consumer on the stale copy with nothing to say so. A consumer that wants "the
# latest local build" references `0.1.0-*`.
#
# Packs everything Vein.Hosting depends on — the compiler, the compiled path (Vein.Jit, the SECS runtime and
# its two ShardECS assemblies) and Vein.Hosting itself (the stdlib travels inside it). Nothing else is touched.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-0.1.0-dev.$(date +%Y%m%d%H%M%S)}"
FEED="${VEIN_FEED:-$HOME/VeinFeed}"
mkdir -p "$FEED"

for project in ShardECS.Contracts/Contracts ShardECS.SECS/SECS Vein.Compiler/Vein.Compiler Vein.Runtime.SECS/Vein.Runtime.SECS Vein.Jit/Vein.Jit Vein.Hosting/Vein.Hosting Vein.Cloud/Vein.Cloud Vein.Cli/Vein.Cli; do
  echo "packing $project $VERSION…"
  # The output is kept for a failure: a running veinc locks Vein.Cli's bin, and a pack that dies quietly
  # leaves the feed one package short with only a missing "ok" line to say so.
  if ! out="$(dotnet pack "$REPO/src/$project.csproj" -c Release -p:Version="$VERSION" -o "$FEED" --nologo -v quiet 2>&1)"; then
    echo "FAILED: $project" >&2
    echo "$out" | grep -E "error|locked by" | sort -u | head -5 >&2
    exit 1
  fi
done

echo "ok: Vein.Hosting and its dependencies, and the veinc tool (Vein.Cli), $VERSION → $FEED"
echo "    a consumer's nuget.config adds that folder as a source; reference Vein.Hosting $VERSION (or 0.1.0-*)."
