# AGENTS.md — <repo name> (a VeinScript project)

<!-- Template, kept in VeinsScriptFirst/docs/AGENTS-TEMPLATE.md. Copy it to a repo's root as AGENTS.md (VS Code
     Copilot and most agents read that), and make that repo's CLAUDE.md the one line `@AGENTS.md`, so the
     text lives in one place. The repos sit side by side on the Desktop, so the paths below are relative. -->

This repo is written in VeinScript. VeinScript is NOT C#, JS or Python: code guessed from them does not
compile. The language lives in ../VeinsScriptFirst, which is the source of truth for it.

## Before writing any .vein
1. Read ../VeinsScriptFirst/docs/VEINSCRIPT-FOR-LLMS.md, the full reference.
2. Read ../VeinsScriptFirst/docs/RULES.md (the traps) and RULES-OF-THUMB.md (how a file stays readable).
3. When the docs and the compiler disagree, the compiler wins. Show the error.

## Checking your work
- Run `veinc check <file>` on every file you change. No warnings left.
- Run `veinc uwu <file>` too: it rewrites C# habits into VeinScript (`--write` saves) and hints at what
  only you can restructure, such as shard `var`s that should be a shape.
- Run this repo's tests (`veinc test <folder>`, or its tests/run.sh). Never edit an expected output just
  to make a test pass: say what changed and why.

## When the language gets in the way
- Do not invent syntax or hide a hack. Write the simplest form that works.
- Add an entry to FRICTION.md: what you tried, what happened, the workaround, and where it is in the code.
  A workaround is a bug report, not a solution. The language is fixed in ../VeinsScriptFirst, where
  `bash tools/check-corpus.sh` and `tools/count-corpus.sh` read every repo, this one included.
- Never change the VeinScript compiler or stdlib from this repo.

## Safety
- Never run recursive deletes (rm -rf, Remove-Item -Recurse) on project files.
- Windows 11 + PowerShell.
