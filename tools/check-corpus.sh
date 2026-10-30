#!/usr/bin/env bash
# check-corpus.sh — `veinc check` on every bundle and app in the corpus (corpus.txt), one report.
#
#   bash tools/check-corpus.sh              uses this repo's build (src/Vein.Cli/bin/Debug)
#   VEINC=/path/veinc.dll bash tools/…      another build — e.g. one copied aside while a game holds the bin
#
# A file with no top-level `bundle` or `app` is a fragment (shards/, publicators/, formats/): its main file
# checks it. Each file is checked from its own folder, as an editor would. Exit 1 when any file has an error.
# Run it before every compiler commit: a change that breaks a game is a language bug until shown otherwise.
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
VEINC="${VEINC:-$REPO/src/Vein.Cli/bin/Debug/net8.0/veinc.dll}"
WORK="${TMPDIR:-/tmp}/vein-corpus"
mkdir -p "$WORK"
: > "$WORK/diagnostics.txt"; : > "$WORK/files.txt"

[ -f "$VEINC" ] || { echo "no veinc at $VEINC — dotnet build src/Vein.Cli first"; exit 1; }

check_one() {
    label="$1"; file="$2"
    out=$(cd "$(dirname "$file")" && dotnet "$VEINC" check "$(basename "$file")" </dev/null 2>&1)
    status=$([ "$(printf '%s' "$out" | tail -1)" = "ok" ] && echo ok || echo problems)
    printf '%s\t%s\t%s\n' "$label" "$status" "$file" >> "$WORK/files.txt"
    printf '%s\n' "$out" | grep -E ': (error|warning) VS[0-9]+' | sed "s#^#$label\t#" >> "$WORK/diagnostics.txt"
}
export -f check_one; export VEINC WORK

bash "$REPO/tools/corpus-files.sh" | while IFS=$'\t' read -r label file; do
    grep -qE '^(bundle|app)[[:space:]]' "$file" && printf '%s\t%s\n' "$label" "$file"
done | xargs -P "${JOBS:-6}" -d '\n' -n 1 bash -c 'IFS=$'"'"'\t'"'"' read -r l f <<<"$0"; check_one "$l" "$f"'

echo "== files checked, by folder (ok / with problems)"
awk -F'\t' '{ n[$1]++; if ($2 == "ok") ok[$1]++ } END { for (l in n) printf "  %-16s %4d checked  %4d ok  %4d with problems\n", l, n[l], ok[l], n[l]-ok[l] }' "$WORK/files.txt" | sort

echo "== diagnostics by code (count, severity, code, first message)"
sed -E 's#^[^\t]*\t.*: (error|warning) (VS[0-9]+): (.*)$#\2\t\1\t\3#' "$WORK/diagnostics.txt" \
    | awk -F'\t' '{ c[$1]++; s[$1]=$2; if (!($1 in m)) m[$1]=substr($3,1,110) } END { for (k in c) printf "  %5d  %-7s %s  %s\n", c[k], s[k], k, m[k] }' | sort -rn

echo "== diagnostics by folder"
awk -F'\t' '{ n[$1]++ } END { for (l in n) printf "  %-16s %d\n", l, n[l] }' "$WORK/diagnostics.txt" | sort

echo "(every line: $WORK/diagnostics.txt)"
! grep -q $'\terror\t\|: error VS' "$WORK/diagnostics.txt"
