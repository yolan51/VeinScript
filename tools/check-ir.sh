#!/usr/bin/env bash
# Golden test for `veinc ir` (the VeinIR ASCII tree). See docs/VEINIR-FORMAT.md.
#   ./tools/check-ir.sh                 # diff actual vs tests/ir/*.ir.txt (fails on mismatch)
#   UPDATE_GOLDEN=1 ./tools/check-ir.sh # regenerate the golden files
set -euo pipefail
cd "$(dirname "$0")/.."

SAMPLES="demo events handles mypage payload shaped site"
GOLDEN_DIR="tests/ir"
CLI="dotnet run --project src/Vein.Cli --no-build --"
mkdir -p "$GOLDEN_DIR"

echo "building…"
dotnet build VeinScript.sln -v quiet -nologo >/dev/null

fail=0

# 1. glyph containment: only IrTree.cs may contain the connector/continuation glyphs.
echo "checking glyph containment…"
offenders=$(grep -rlE '(\|--- |`--- |├─── |└─── )' src --include='*.cs' | grep -v 'Ir/IrTree.cs' || true)
if [ -n "$offenders" ]; then echo "FAIL: tree glyphs found outside IrTree.cs:"; echo "$offenders"; fail=1; fi

for s in $SAMPLES; do
  out=$($CLI ir "samples/$s.vein")
  gold="$GOLDEN_DIR/$s.ir.txt"

  if [ "${UPDATE_GOLDEN:-0}" = "1" ]; then
    printf '%s' "$out" > "$gold"
    echo "wrote $gold"
    continue
  fi

  if [ ! -f "$gold" ]; then echo "FAIL: missing golden $gold (run UPDATE_GOLDEN=1)"; fail=1; continue; fi
  if ! diff -u "$gold" <(printf '%s' "$out") >/dev/null; then
    echo "FAIL: $s differs from golden:"; diff -u "$gold" <(printf '%s' "$out") || true; fail=1
  else
    echo "ok: $s"
  fi

  # 2. no trailing whitespace, no line over 200 columns.
  if printf '%s' "$out" | grep -nE ' +$' >/dev/null; then echo "FAIL: $s has trailing whitespace"; fail=1; fi
  if printf '%s' "$out" | awk 'length>200{print NR": "length}' | grep -q .; then echo "FAIL: $s has a line >200 cols"; fail=1; fi

  # 3. idempotent: rendering twice is byte-identical.
  out2=$($CLI ir "samples/$s.vein")
  if [ "$out" != "$out2" ]; then echo "FAIL: $s is not idempotent"; fail=1; fi
done

[ "$fail" = 0 ] && echo "ALL IR CHECKS PASSED" || { echo "IR CHECKS FAILED"; exit 1; }
