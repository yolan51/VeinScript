#!/usr/bin/env bash
# corpus-files.sh — prints `label<TAB>path` for every .vein file of the corpus (corpus.txt). Shared by
# check-corpus.sh and count-corpus.sh, so both look at exactly the same files.
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"

grep -vE '^\s*(#|$)' "$REPO/corpus.txt" | while IFS='=' read -r label folder; do
    label="$(echo "$label" | xargs)"; folder="$(echo "$folder" | xargs)"
    dir="$REPO/$folder"
    if [ ! -d "$dir" ]; then echo "missing: $label ($folder)" >&2; continue; fi
    find "$dir" -type f -name '*.vein' \
        -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/.godot/*' -not -path '*/.git/*' \
        -not -path '*/out/*' -not -path '*/artifacts/*' -not -path '*/addons/*' -not -path '*/build/*' \
        -not -path '*/node_modules/*' -not -path '*/.vein/*' -not -name '*.implicit.app.vein' \
        | sort | sed "s#^#$label\t#"
done
