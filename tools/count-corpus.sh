#!/usr/bin/env bash
# count-corpus.sh — how often each known workaround appears across the corpus (corpus.txt), per folder.
#
#   bash tools/count-corpus.sh
#
# Each row is a pattern that stands for something the language makes the author spell out by hand. Fix the
# biggest counts first, then re-run: the count going down is the measure of progress. Comments are left
# out (a `//` at the start of a line or after a space), so a row counts code, not prose about it.
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/vein-corpus"
mkdir -p "$WORK"

# label<TAB>code, one line per source line, comments stripped.
bash "$REPO/tools/corpus-files.sh" | while IFS=$'\t' read -r label file; do
    sed -E 's#(^|[[:space:]])//.*$##' "$file" | sed "s#^#$label\t#"
done > "$WORK/lines.txt"

labels=$(cut -f1 "$WORK/lines.txt" | sort -u)

# name | extended regex (matched in the code after the label's tab) | what it stands for
PATTERNS=$(cat <<'EOF'
text continued with + at a line end|\+[[:space:]]*$|string interpolation, "HP: \(hp)"
a quoted piece joined with +|"[[:space:]]*\+|\+[[:space:]]*"|string interpolation
the long print, emit *Vein.Console.Io.@Print|emit \*Vein\.Console\.Io\.@Print|`do print(…)`, or a quick fix offering it
the short print, do print(|do print\(|(for comparison)
a test's own `SF check(name, good)`|SF check\(|a built-in check for tests (`veinc test`)
an ok / FAIL line printed by hand|"ok   |"FAIL |a built-in check for tests
n = n + 1|\b([A-Za-z_][A-Za-z0-9_]*) = \1 \+ 1\b|`n += 1`, or a counting query
x = x <op> …|\b([A-Za-z_][A-Za-z0-9_]*) = \1 [-+*/] |a compound assignment the author did not reach for
if c { x = a } else { x = b }|if .+\{ *([A-Za-z_][A-Za-z0-9_.]*) = .+\} *else *\{ *\1 = |an if-expression, x = if c { a } else { b }
a found-flag loop (var found = false)|(var|let) (found|hit|exists|seen|already)[A-Za-z]* = false|a read-only query an fn can ask: `any $Task where …`
a fully qualified *Vein.X.Y. reference|\*Vein\.[A-Za-z]+\.[A-Za-z]+\.|a `need` that makes the bare name work
fromJson("{}") as a mutable store|fromJson\("\{\}"\)|a dictionary/record literal
fromJson("[]") as a list|fromJson\("\[\]"\)|a list literal `[]` that the language accepts everywhere
toJson( (often scanned as text)|toJson\(|reading a record's keys and kinds directly
keysOf / kindOf helpers|keysOf|kindOf|built-in `keys(d)` and `kind(v)`
do quit(0) to end a tool|do quit\(0\)|`veinc run` not waiting on stdin
a test waiting for frame N|\.frame == [0-9]+|a test runner that runs checks after boot
target $World as w { } (composition workaround)|target \$World as [A-Za-z]+ \{ *\}|composition that does not need $World named
a line nested 7+ levels deep|^(    ){7,}[^ ]|^\t{7,}[^\t]|`return` in SF/hear (guard clauses), early `continue`
a one-line if with no else that returns|if .+\{ *return .*\}|(fine — counted for comparison with SF/hear)
substring(|substring\(|(text slicing, for reference)
string( conversion|\bstring\(|interpolation
EOF
)

printf '%-52s %6s' "pattern" "total"
for l in $labels; do printf ' %9s' "$(echo "$l" | cut -c1-9)"; done; echo
echo "$PATTERNS" | while IFS= read -r row; do
    # name|regex|meaning — the regex may hold `|` alternations itself, so it is every field in between.
    name=$(awk -F'|' '{ print $1 }' <<<"$row")
    regex=$(awk -F'|' '{ r=$2; for (i=3; i<NF; i++) r=r "|" $i; print r }' <<<"$row")
    meaning=$(awk -F'|' '{ print $NF }' <<<"$row")
    total=$(cut -f2- "$WORK/lines.txt" | grep -cE -- "$regex")
    printf '%-52s %6d' "$(echo "$name" | cut -c1-52)" "$total"
    for l in $labels; do
        printf ' %9d' "$(grep -E "^$l"$'\t' "$WORK/lines.txt" | cut -f2- | grep -cE -- "$regex")"
    done
    echo "   → $meaning"
done
echo
echo "lines of code per folder (comments out):"
for l in $labels; do printf '  %-16s %6d\n' "$l" "$(grep -E "^$l"$'\t' "$WORK/lines.txt" | cut -f2- | grep -cvE '^[[:space:]]*$')"; done
