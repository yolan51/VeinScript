# What to give Base44's assistant

This folder answers its three questions: golden files, your real checker, and a written spec.

## 1. Golden files: `golden/` (151 files)

**Upload:** every `.vein` file in `golden/` to **Admin → Assistant Memory**, then index them.

- **Start here, 20 files** (`01-…` to `20-…`): hand-picked, one per construct family.
- **101 more** from the language repo's samples, including `desktop-widgets.vein` (every desktop widget) and `desktop-todo.vein` (a whole app).
- **30** from VeinScriptBundle: its reusable bundles and their samples.
- **Each one passes `veinc check` alone, with no errors and no warnings,** checked in its own empty folder so
  nothing beside it could help it compile.
- **Full-line comments are removed,** as Base44 asked.
- **`golden/INDEX.txt`** gives one line per file: what it demonstrates, and which file it came from.

Left out: 15 files that `need` another bundle (an RPG bundle needing `rpg.Stats`, a game needing its kit). They
compile in their own repo, but not on their own, so they can't be golden.

There's no formatter yet (`veinc fmt` is on the roadmap). These files are the samples' own formatting, which is
the canonical style today.

## 2. Your real checker: `checker/vein-check.js`

This runs the actual compiler (`veinc check`), not a reimplementation. It needs Node and nothing else.

**The command, one file:**
```
node checker\vein-check.js golden\02-events.vein
```

**Success**, verbatim:
```
{"ok":true,"problems":[],"formatted":"bundle Demo {\n    event @Request ..."}
```

**Failure**, verbatim:
```
{"ok":false,"problems":["main.vein:2:29: error VS0006: VeinScript has no `!=` - write `not (a == b)`. Logic is spelled in words here."],"formatted":"..."}
```

How it decides:
- **`ok` means no errors and no warnings,** which is what the sample tests require of a shipped file. A warning is
  a problem too.
- **`formatted` is always the source unchanged,** because there's no formatter yet.

**The service, for Base44:**
```powershell
$env:VEIN_VALIDATOR_SECRET = Get-Content "$env:USERPROFILE\.veindna\VEIN_VALIDATOR_SECRET.txt"
node checker\vein-check.js --serve          # → http://127.0.0.1:8787/check
```

- **The request:** `POST /check` with `{ "code": "..." }` and the header `x-vein-secret: <the secret>`. It answers
  with the same JSON.
- **It only compiles.** `veinc check` never runs a program.
- **Limits:** 256 KB per request and 30 seconds, and 4 checks at a time.

**Base44 runs in the cloud, so it can't reach `127.0.0.1` on your PC.** You need a public address:
- **For testing:** a tunnel, for example `cloudflared tunnel --url http://127.0.0.1:8787`, which prints an
  `https://…trycloudflare.com` address. Give Base44 that address plus `/check`.
- **For lasting use:** host it on a server.

Either way, the secret is the only thing standing between the internet and your compiler.

**The secret:** make it once, keep it in a file, and give it to Base44 in its Secrets settings. Never paste it in
a chat.
```powershell
$s = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
Set-Content "$env:USERPROFILE\.veindna\VEIN_VALIDATOR_SECRET.txt" $s
```

## 3. The written spec: `spec/VEINSCRIPT-SPEC.md`

**Upload it** as the assistant's reference file.

- It's the repo's reference written for LLMs (`docs/VEINSCRIPT-FOR-LLMS.md`).
- **Section 17** is new: what came after it (collections, `target … not #Mark`, desktop programs), plus a
  formatting section.
- **Its desktop part covers the full widget set,** with every builder and its arguments, the events (and which
  field the host writes first), what attaches to any widget, the pitfalls (the 63 reserved words, `discard`,
  floats for sliders), and two complete examples. Both examples pass `veinc check`.
- About 8,100 words, more than the 4,000 Base44 suggested. It's the maintained source, so it's not cut down.

## Tell Base44 this

Its own prompt lists constructs VeinScript does not have: `signal`, `on start`, `on receive`, `skip when`,
`for` and "dispatch". That's very likely why its code fails. Section 17 of the spec gives the real form of each.
