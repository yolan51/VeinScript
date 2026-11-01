#!/usr/bin/env bash
# gen-scale.sh — write a VeinScript program of N bundles × M shards, to measure what scale costs.
#
#   bash tools/gen-scale.sh <dir> <bundles> <shards> <messages|ticks|mixed> [identities-per-bundle]
#
# One bundle per file (main.vein is B000 and `need`s every other; bundles/Bxxx.vein the rest), all `by scale`.
# What each shard does, by workload:
#
#   messages  every tick, shard Sk of bundle Bi emits @Mk { n: 1 }; shard Sk of the NEXT bundle hears it and adds
#             it to its bundle's total — N × M messages a frame, every one crossing a bundle (the "100 bundles
#             sending data to each other" case)
#   ticks     every tick, every shard adds 1 to each of its bundle's identities (`target`) — N × M × K activations
#   mixed     both
#
# At frame 10, B000's Report prints one checksum over every bundle — the same number on any runtime, any
# thread count, which is what proves a faster run is still the same run.
set -eu

dir="$1"; n="$2"; m="$3"; work="$4"; k="${5:-10}"
case "$work" in messages|ticks|mixed) ;; *) echo "workload: messages | ticks | mixed"; exit 2 ;; esac
mkdir -p "$dir/bundles"

name() { printf "B%03d" "$1"; }

for ((i = 0; i < n; i++)); do
    b=$(name "$i"); prev=$(name $(( (i + n - 1) % n )))
    if (( i == 0 )); then file="$dir/main.vein"; else file="$dir/bundles/$b.vein"; fi
    {
        echo "bundle $b by scale {"
        if (( i == 0 )); then
            echo "    need \"Vein.Console\""
            for ((j = 1; j < n; j++)); do echo "    need \"scale.$(name "$j")\""; done
        fi
        echo "    shape \$Tot$i { sum: int folds sum }"
        echo "    shape \$Work$i { x: float }"
        if [[ $work != ticks ]]; then for ((s = 0; s < m; s++)); do echo "    event @M$s { n: int }"; done; fi
        echo "    shard Seed { run once {"
        echo "        let t = spawn()"
        echo "        attach \$Tot$i to t { sum: 0 }"
        if [[ $work != messages ]]; then
            echo "        repeat $k { let w = spawn()"
            echo "            attach \$Work$i to w { x: 0.0 } }"
        fi
        echo "    } }"
        for ((s = 0; s < m; s++)); do
            echo "    shard S$s {"
            if [[ $work != ticks ]]; then
                echo "        each tick { emit @M$s { n: 1 } }"
                echo "        hear *scale.$prev.@M$s as msg { target \$Tot$i as t { t.Tot$i.sum += msg.n } }"
            fi
            if [[ $work != messages ]]; then
                echo "        each tick { target \$Work$i as w { w.Work$i.x = w.Work$i.x + 1.0 } }"
            fi
            echo "    }"
        done
        if (( i == 0 )); then
            echo "    shard Report {"
            echo "        var frame = 0"
            echo "        each tick { frame += 1 }"
            echo "        settled { if frame == 10 {"
            echo "            var sum = 0.0"
            for ((j = 0; j < n; j++)); do
                echo "            target \$Tot$j as t { sum = sum + t.Tot$j.sum }"
                if [[ $work != messages ]]; then echo "            target \$Work$j as w { sum = sum + w.Work$j.x }"; fi
            done
            echo "            do print(\"checksum \" + sum)"
            echo "        } }"
            echo "    }"
        fi
        echo "}"
    } > "$file"
done
echo "wrote $n bundles x $m shards ($work) in $dir"
