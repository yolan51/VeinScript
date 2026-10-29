# Store files to republish (2026-10-25)

These 23 files in the live ShardStore are older copies. Their versions in this repository compile under the
current language (`need`, `return`, `do`); the store copies don't. Push them from the Workbench (signed in,
**File → Push**), selecting these paths:

```
samples/LANGUAGE-TOUR.vein
samples/bench_folds.vein
samples/builder_defaults.vein
samples/characters.vein
samples/db_query.vein
samples/db_supabase.vein
samples/demo.vein
samples/diagnostics.vein
samples/diagnostics_guard.vein
samples/dom_rewire.vein
samples/entities.vein
samples/entities_bind.vein
samples/entities_bring_rows.vein
samples/entities_filter.vein
samples/entities_tree.vein
samples/functions.vein
samples/handles.vein
samples/json_roundtrip.vein
samples/mypage.vein
samples/payload.vein
samples/shaped.vein
samples/site.vein
samples/web_site.vein
```

…and one more, which also differs:

```
samples/web_app/shards/Reference.vein   ← a FRAGMENT of samples/web_app; it never compiles alone.
                                          Remove it from the store, or publish it with web_app.
```

The other 29 store files are identical to the repository and already compile.

Afterwards, check that the store compiles:

```powershell
cd C:\Users\yo\Desktop\VeinDNA
dotnet run --project src\Vein.Dna.Cli -- sequence shardstore: --diagnostics 2>&1 | Select-String " error "
```

No output means every store file compiles. Then `sync shardstore: --push` refreshes their DNA.
