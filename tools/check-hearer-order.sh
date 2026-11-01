#!/usr/bin/env bash
# check-hearer-order.sh — which programs give a different RESULT when each event's hearers run in reverse order?
#
#   bash tools/check-hearer-order.sh                       every program in the corpus (corpus.txt)
#   bash tools/check-hearer-order.sh a.vein b.vein          just these
#   VEINC=/path/veinc.dll TICKS=30 bash tools/…             another build, another length
#
# Hearers of one event run in registration order, and a hearer that changes the payload changes what the
# hearers after it read (the ping_six / ping_alias / ping_after repros). docs/PAYLOAD-FOLDS.md removes that;
# this measures how much code depends on it today. Each program runs twice — as written, and with
# `--reverse-hearers` — and the outputs are compared:
#
#   same          identical
#   no-output / not-run   printed nothing, or does not compile — the comparison says nothing
#   print-order   the same lines in another order: hearers that both print — fine, the folds keep run order
#   DIFFERS       a line only one run printed: a value depends on which hearer ran first
#
# Programs run as COPIES: each one's folder is copied to a fresh temp folder first, so a save, a vein.lock line
# or any other file it writes lands in the copy, never in the project. A program that talks to the outside —
# a console window, the network, a file picker — is skipped and listed, not run.
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
VEINC="${VEINC:-$REPO/src/Vein.Cli/bin/Debug/net8.0/veinc.dll}"
TICKS="${TICKS:-30}"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/vein-hearer-order.XXXXXX")"
[ -f "$VEINC" ] || { echo "no veinc at $VEINC — dotnet build src/Vein.Cli first"; exit 1; }

if [ $# -gt 0 ]; then for f in "$@"; do printf 'given\t%s\n' "$(cd "$(dirname "$f")" && pwd)/$(basename "$f")"; done
else bash "$REPO/tools/corpus-files.sh"; fi | while IFS=$'\t' read -r label file; do
    grep -qE '^(bundle|app)[[:space:]]' "$file" || continue
    dir="$(dirname "$file")"
    # What it is made of: itself, its fragments (no `bundle`/`app` of their own, in its folder or in a direct
    # subfolder like shards/ or publicators/ that is not a project of its own) and its bundles/ — not the other
    # programs that share its folder.
    parts=$(find "$dir" -maxdepth 2 -name '*.vein' 2>/dev/null | while read -r p; do
        d="$(dirname "$p")"
        if [ "$p" -ef "$file" ] || [[ "$p" == "$dir/bundles/"* ]]; then echo "$p"
        elif ! grep -qE '^(bundle|app)[[:space:]]' "$p" \
             && { [ "$d" = "$dir" ] || ! grep -lqE '^(bundle|app)[[:space:]]' "$d"/*.vein 2>/dev/null; }; then echo "$p"; fi
    done)
    if [ -n "$parts" ] && printf '%s\n' "$parts" | xargs -d '\n' grep -lqE '@(Console|Listen|Link|Fetch|Send)\b|PickOpenFile|PickSaveFile' 2>/dev/null; then
        printf '%-10s %-12s %s\n' "$label" "skipped" "$file (talks outside)"; continue
    fi
    # The REPO is copied, not just the folder: a program finds its bundles from further up than its own folder
    # (VeinFarm's game/ has a .veinproj of its own, and its bundles come from the repo). Once per repo.
    root="$dir"
    while [ "$root" != "/" ] && [ "$(basename "$root")" != "Desktop" ] && [ ! -e "$root/.git" ]; do root="$(dirname "$root")"; done
    [ "$root" = "/" ] || [ "$(basename "$root")" = "Desktop" ] && root="$dir"
    key=$(printf '%s' "$root" | md5sum | cut -c1-12); copy="$WORK/$key"
    if [ ! -d "$copy" ]; then
        mkdir -p "$copy"
        tar -C "$root" --exclude=bin --exclude=obj --exclude=.git --exclude=.godot --exclude=node_modules \
            --exclude=artifacts --exclude=build -cf - . 2>/dev/null | tar -C "$copy" -xf - 2>/dev/null
    fi
    rel="${file#"$root"/}"
    run() { (cd "$copy/$(dirname "$rel")" && timeout 60 dotnet "$VEINC" run "$(basename "$rel")" --ticks "$TICKS" "$@" </dev/null 2>&1) | grep -vE '^(app |vein\.lock)' | tr -d '\r'; }
    out="$copy/.$(printf '%s' "$rel" | tr '/' '_')"
    run > "$out.as-written.txt"
    run --reverse-hearers > "$out.reversed.txt"
    if grep -qE ': error VS[0-9]+' "$out.as-written.txt"; then verdict="not-run"     # does not compile: says nothing
    elif [ ! -s "$out.as-written.txt" ]; then verdict="no-output"                    # printed nothing: says nothing
    elif cmp -s "$out.as-written.txt" "$out.reversed.txt"; then verdict="same"
    elif cmp -s <(sort "$out.as-written.txt") <(sort "$out.reversed.txt"); then verdict="print-order"
    else verdict="DIFFERS"; fi
    printf '%-10s %-12s %s\n' "$label" "$verdict" "$file"
    [ "$verdict" = "DIFFERS" ] && diff "$out.as-written.txt" "$out.reversed.txt" | head -8 | sed 's/^/             /'
done | tee "$WORK/report.txt"

echo "== totals"
awk '/^[^ ]/ { c[$2]++ } END { for (k in c) printf "  %-12s %d\n", k, c[k] }' "$WORK/report.txt" | sort
echo "(report and the copies: $WORK)"
