#!/usr/bin/env node
// vein-check.js — check VeinScript with the REAL compiler (`veinc check`), and answer in JSON only:
//
//   { "ok": true|false, "problems": ["main.vein:2:29: error VS0006: VeinScript has no `!=` …"], "formatted": "…" }
//
// Two ways to use it, no dependencies beyond Node:
//
//   node vein-check.js path/to/file.vein          check one file
//   type file.vein | node vein-check.js           check stdin           (PowerShell: Get-Content f.vein | node …)
//   node vein-check.js --serve                    an HTTP service: POST { "code": "…" } → the same JSON
//
// `ok` means what the repo's sample tests demand of a shipped file: it compiles with NO errors and NO warnings.
// Every warning and error is in `problems`, exactly as veinc printed it. VeinScript has no formatter yet
// (`veinc fmt` is on the roadmap), so `formatted` is always the source, unchanged.
//
// The service: listens on 127.0.0.1:8787 (PORT, HOST to change), requires the header `x-vein-secret` to equal
// the environment variable VEIN_VALIDATOR_SECRET, accepts at most 256 KB, and gives veinc 30 seconds. It only
// COMPILES what it is sent — `veinc check` never runs a program.
//
// Which veinc: VEINC (a full command, e.g. `veinc` for the installed tool), else the repo's own build,
// `dotnet <repo>/src/Vein.Cli/bin/Debug/net8.0/veinc.dll` (build it once: dotnet build src/Vein.Cli).

"use strict";
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const crypto = require("crypto");
const { execFile } = require("child_process");

const REPO = path.resolve(__dirname, "..", "..", "..");
const VEINC = process.env.VEINC
  ? process.env.VEINC.split(" ").filter(Boolean)
  : ["dotnet", path.join(REPO, "src", "Vein.Cli", "bin", "Debug", "net8.0", "veinc.dll")];
const MAX_BYTES = 256 * 1024;
const TIMEOUT_MS = 30000;

/** Check one source text. Resolves to { ok, problems, formatted }. */
function check(source) {
  return new Promise((resolve) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "vein-check-"));
    const file = path.join(dir, "main.vein");
    fs.writeFileSync(file, source);
    const [cmd, ...pre] = VEINC;
    execFile(cmd, [...pre, "check", file], { cwd: dir, timeout: TIMEOUT_MS, windowsHide: true },
      (err, stdout, stderr) => {
        fs.rmSync(dir, { recursive: true, force: true });   // only the folder this call just made
        const lines = `${stdout}\n${stderr}`.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
        const problems = lines.filter((l) => /:\s(error|warning)\s[A-Z]{2}\d+:/.test(l));
        if (err && err.killed) problems.push(`veinc did not answer within ${TIMEOUT_MS / 1000} s`);
        else if (err && problems.length === 0) problems.push(`veinc failed: ${(lines.join(" ") || err.message).slice(0, 400)}`);
        const ok = !err && problems.length === 0 && lines.includes("ok");
        resolve({ ok, problems, formatted: source });
      });
  });
}

function sameSecret(given, expected) {
  const a = Buffer.from(String(given || ""));
  const b = Buffer.from(expected);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

function serve() {
  const secret = process.env.VEIN_VALIDATOR_SECRET;
  if (!secret || secret.length < 16) {
    console.error("Set VEIN_VALIDATOR_SECRET (at least 16 characters) before starting the service.");
    process.exit(2);
  }
  const port = Number(process.env.PORT || 8787);
  const host = process.env.HOST || "127.0.0.1";
  let busy = 0;

  const send = (res, status, body) => {
    res.writeHead(status, { "content-type": "application/json" });
    res.end(JSON.stringify(body));
  };

  http.createServer((req, res) => {
    if (req.method !== "POST" || !["/", "/check"].includes(req.url)) return send(res, 404, { error: "POST /check" });
    if (!sameSecret(req.headers["x-vein-secret"], secret)) return send(res, 401, { error: "not allowed" });
    if (busy >= 4) return send(res, 503, { error: "busy, try again" });

    const chunks = [];
    let size = 0;
    req.on("data", (c) => {
      size += c.length;
      if (size > MAX_BYTES) { send(res, 413, { error: `at most ${MAX_BYTES} bytes` }); req.destroy(); }
      else chunks.push(c);
    });
    req.on("end", async () => {
      if (res.writableEnded) return;
      let code;
      try { code = JSON.parse(Buffer.concat(chunks).toString("utf8")).code; } catch { return send(res, 400, { error: "the body is not JSON" }); }
      if (typeof code !== "string" || code.length === 0) return send(res, 400, { error: "send { \"code\": \"…\" }" });
      busy++;
      try { send(res, 200, await check(code)); }
      finally { busy--; }
    });
  }).listen(port, host, () => console.log(`vein-check listening on http://${host}:${port}/check`));
}

async function main() {
  const args = process.argv.slice(2);
  if (args[0] === "--serve") return serve();
  const source = args[0] ? fs.readFileSync(args[0], "utf8") : fs.readFileSync(0, "utf8");
  const result = await check(source);
  process.stdout.write(JSON.stringify(result) + "\n");
  process.exitCode = result.ok ? 0 : 1;
}

main();
