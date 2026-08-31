#!/usr/bin/env bash
# check-perf.sh — make the backend's speedup a NUMBER YOU CAN REPRODUCE.
#
# ROADMAP.md opens with "Status is measured, not planned. Every 'done' names the command that proves it
# and the check that guards it" — and then claims ≈10× (~2.30 µs → ~0.23 µs per activation) with no
# command and no check. So a perf regression was invisible: nothing in the repo would have noticed the
# backend getting slower, and the one number the M5 milestone is judged on could not be re-derived.
#
# WHAT IS MEASURED: the cost of an ACTIVATION — one identity's pass through one `target` body in one
# frame. samples/bench_folds.vein is 1000 identities × 2 shards, so a frame is 2000 activations — the
# same shape BACKEND-CONTRACT §0 recorded its baseline at, so the numbers are directly comparable.
#
# HOW, and why this way: each side is timed at TWO frame counts and the difference is taken. Process
# start, JIT, and building the world are all fixed costs that have nothing to do with per-activation
# speed, and on the compiled side they would otherwise dwarf the thing being measured. Subtracting a
# short run from a long one cancels every fixed cost exactly. Each timing is the best of N runs, because
# a scheduler can only ever make a run slower than the machine is capable of.
#
#   bash tools/check-perf.sh            # report both, and the ratio
#
set -u

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WIN_REPO="$(cd "$REPO" && pwd -W 2>/dev/null || echo "$REPO")"
WORK="${TMPDIR:-/tmp}/vein-perf-check"
SRC="samples/bench_folds.vein"

# BOTH SIDES IN RELEASE. This is the whole methodology, and getting it wrong is how a speedup number
# gets inflated for free: timing a Debug interpreter against a Release backend measured the build
# configuration as much as the backend, and reported 9.1x on this machine where the honest figure is
# 6.5x. The other checks use Debug because they compare OUTPUT, where the build cannot change the
# answer; here it changes the only thing being measured.
CLI="$REPO/src/Vein.Cli/bin/Release/net8.0/veinc.dll"

ENTITIES=1000
SHARDS=2            # two `each tick` targets, so a frame is ENTITIES * SHARDS activations
SHORT=100
LONG=1000
REPEATS=3

# The floor the check FAILS on. Deliberately far below the ≈10× the roadmap reports: this guards against
# "the backend stopped being meaningfully faster", not against normal machine-to-machine variation. A
# tight bound here would fail on a loaded laptop and teach everyone to ignore the check.
MIN_RATIO=3

mkdir -p "$WORK"
echo "building…"
dotnet build "$REPO/VeinScript.sln" -c Release -v quiet --nologo >/dev/null 2>&1 || { echo "BUILD FAILED"; exit 1; }

# ---- emit + compile the backend once -------------------------------------------------------------
name="bench_folds"
out="$WORK/$name"
mkdir -p "$out"
dotnet "$CLI" emit "$REPO/$SRC" -o "$out" >/dev/null 2>&1 || { echo "FAIL: emit"; exit 1; }

cat > "$out/gen.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>${name}_gen</AssemblyName>
    <Optimize>true</Optimize>
    <Configuration>Release</Configuration>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$WIN_REPO/src/Vein.Runtime.SECS/Vein.Runtime.SECS.csproj" />
  </ItemGroup>
</Project>
EOF

if ! dotnet build "$out/gen.csproj" -c Release -v quiet --nologo >"$out/build.log" 2>&1; then
    echo "FAIL: generated C# does not compile (see $out/build.log)"
    exit 1
fi
GEN="$out/bin/Release/net9.0/${name}_gen.dll"

# ---- timing --------------------------------------------------------------------------------------

# Milliseconds for one run, best of $REPEATS.
best_ms() {
    local best=""
    for _ in $(seq "$REPEATS"); do
        local start end ms
        start=$(date +%s%N)
        "$@" >/dev/null 2>&1
        end=$(date +%s%N)
        ms=$(( (end - start) / 1000000 ))
        if [ -z "$best" ] || [ "$ms" -lt "$best" ]; then best=$ms; fi
    done
    echo "$best"
}

# Marginal nanoseconds per activation: (t_long - t_short) / (activations_long - activations_short).
marginal_ns() {
    local t_short=$1 t_long=$2
    local acts=$(( (LONG - SHORT) * ENTITIES * SHARDS ))
    echo $(( (t_long - t_short) * 1000000 / acts ))
}

echo "measuring — $ENTITIES identities x $SHARDS shards; $SHORT vs $LONG frames, best of $REPEATS"

interp_short=$(best_ms dotnet "$CLI" run "$REPO/$SRC" --ticks "$SHORT")
interp_long=$(best_ms dotnet "$CLI" run "$REPO/$SRC" --ticks "$LONG")
secs_short=$(best_ms dotnet "$GEN" --ticks "$SHORT")
secs_long=$(best_ms dotnet "$GEN" --ticks "$LONG")

interp_ns=$(marginal_ns "$interp_short" "$interp_long")
secs_ns=$(marginal_ns "$secs_short" "$secs_long")

# A delta at or below zero means the frame loop is not doing the work — the measurement is meaningless
# rather than fast, and reporting a huge ratio from it would be worse than failing.
if [ "$interp_ns" -le 0 ] || [ "$secs_ns" -le 0 ]; then
    echo "FAIL: a marginal cost came out <= 0 — the frame count is not driving work."
    echo "  interpreter ${interp_short}ms -> ${interp_long}ms ; backend ${secs_short}ms -> ${secs_long}ms"
    exit 1
fi

ratio=$(( interp_ns * 10 / secs_ns ))   # tenths, so one decimal without bc

printf '  interpreter  %5sms -> %5sms   %4s ns/activation\n' "$interp_short" "$interp_long" "$interp_ns"
printf '  C# backend   %5sms -> %5sms   %4s ns/activation\n' "$secs_short" "$secs_long" "$secs_ns"
printf '  speedup      %s.%sx\n' "$((ratio / 10))" "$((ratio % 10))"

if [ "$((ratio / 10))" -lt "$MIN_RATIO" ]; then
    echo "PERF CHECK FAILED — backend is under ${MIN_RATIO}x the interpreter."
    exit 1
fi
echo "PERF CHECK PASSED"
