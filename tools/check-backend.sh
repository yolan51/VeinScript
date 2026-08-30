#!/usr/bin/env bash
# check-backend.sh — prove the C# backend AGREES WITH THE INTERPRETER.
#
# A backend's only real specification is the runtime that already exists: for the same program and the
# same frame count, generated code must print exactly what `veinc run` prints. So this emits C#, compiles
# it against the SECS adapter, runs both, and diffs. A golden file of expected C# would pin the emitter's
# formatting; this pins its MEANING, which is the thing that can be quietly wrong.
#
#   bash tools/check-backend.sh
set -u

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WIN_REPO="$(cd "$REPO" && pwd -W 2>/dev/null || echo "$REPO")"
WORK="${TMPDIR:-/tmp}/vein-backend-check"
CLI="$REPO/src/Vein.Cli/bin/Debug/net8.0/veinc.dll"

# Programs in the identity subset the backend covers, with the frames to run them for.
CASES=("samples/entities.vein:3")

echo "building…"
dotnet build "$REPO/VeinScript.sln" -v quiet --nologo >/dev/null 2>&1 || { echo "BUILD FAILED"; exit 1; }

fail=0
for case in "${CASES[@]}"; do
    src="${case%%:*}"
    ticks="${case##*:}"
    name="$(basename "$src" .vein)"
    out="$WORK/$name"

    mkdir -p "$out"
    dotnet "$CLI" emit "$REPO/$src" -o "$out" >/dev/null 2>&1 || { echo "FAIL: $name — emit failed"; fail=1; continue; }

    cat > "$out/gen.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>${name}_gen</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$WIN_REPO/src/Vein.Runtime.SECS/Vein.Runtime.SECS.csproj" />
  </ItemGroup>
</Project>
EOF

    if ! dotnet build "$out/gen.csproj" -v quiet --nologo >"$out/build.log" 2>&1; then
        echo "FAIL: $name — generated C# does not compile (see $out/build.log)"
        fail=1
        continue
    fi

    dotnet "$CLI" run "$REPO/$src" --ticks "$ticks" </dev/null >"$out/interp.txt" 2>/dev/null
    dotnet "$out/bin/Debug/net9.0/${name}_gen.dll" --ticks "$ticks" >"$out/secs.txt" 2>&1

    if diff -q "$out/interp.txt" "$out/secs.txt" >/dev/null; then
        echo "ok: $name (${ticks} frames) — backend output matches the interpreter"
    else
        echo "FAIL: $name — backend and interpreter disagree:"
        diff "$out/interp.txt" "$out/secs.txt" | head -20
        fail=1
    fi
done

[ "$fail" -eq 0 ] && echo "ALL BACKEND CHECKS PASSED" || echo "BACKEND CHECKS FAILED"
exit "$fail"
