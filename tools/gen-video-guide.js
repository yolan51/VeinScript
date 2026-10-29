// tools/gen-video-guide.js — build the VIDEO GUIDE: one curriculum, three outputs.
//
//   node tools/gen-video-guide.js
//
// Reads   docs/video/curriculum.json               the teaching layer: lessons, talking points, notes
//         samples/**                               the code — ALWAYS read live, never copied into the JSON
//         src/Vein.Workbench/Assets/VeinScript.xshd the syntax colours and the keyword list
// Writes  docs/video/VIDEO-GUIDE.md                for Claude: the curriculum + excerpts (paste or upload)
//         docs/video/ALL-SAMPLES.md                for Claude: every sample's full source, with its notes
//         docs/video/veinscript-video-guide.html   for people: the same, with the code coloured
//
// WHY THE CODE IS NEVER IN THE JSON. A teaching guide with code typed out by hand is a guide that
// teaches syntax the compiler rejects, the day a sample changes. Every snippet here is cut from the file
// on disk by an ANCHOR (the exact text of its first line) and a line count, so an edited sample either
// shows its new code or stops the build — `anchor not found` — and never shows stale code quietly.
//
// WHY THE COLOURS COME FROM THE .xshd. The Workbench already has a theme, and SyntaxThemeTests holds its
// keyword list to the lexer's. Reading it here means the page, the IDE and the video show one language
// in one set of colours, and a keyword added to the lexer reaches this page with no edit to this file.
//
// It FAILS rather than guesses: a sample on disk that the curriculum does not mention, an anchor that
// matches zero or two lines, a lesson naming a file that does not exist. Exit code 1, with the list.

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const rel = (p) => path.relative(ROOT, p).split(path.sep).join('/');
const abs = (p) => path.join(ROOT, p);
const read = (p) => fs.readFileSync(abs(p), 'utf8').replace(/\r\n/g, '\n');

const CURRICULUM = 'docs/video/curriculum.json';
const XSHD = 'src/Vein.Workbench/Assets/VeinScript.xshd';
const OUT_GUIDE = 'docs/video/VIDEO-GUIDE.md';
const OUT_ALL = 'docs/video/ALL-SAMPLES.md';
const OUT_HTML = 'docs/video/veinscript-video-guide.html';

const cur = JSON.parse(read(CURRICULUM));
const errors = [];

// ---- the theme, from the Workbench ---------------------------------------------------------------

const xshd = read(XSHD);
const colors = {};
for (const m of xshd.matchAll(/<Color name="(\w+)"\s+foreground="(#[0-9A-Fa-f]{6})"/g)) colors[m[1]] = m[2];
const KEYWORDS = new Set([...xshd.matchAll(/<Word>([^<]+)<\/Word>/g)].map((m) => m[1]));
for (const need of ['Comment', 'String', 'Number', 'Keyword', 'Shape', 'Mark', 'Event', 'Builder', 'Sigil'])
  if (!colors[need]) errors.push(`${XSHD}: no <Color name="${need}"> — the page would render that token uncoloured`);
if (KEYWORDS.size < 40) errors.push(`${XSHD}: only ${KEYWORDS.size} keywords read — the <Word> format changed?`);

// ---- highlighting --------------------------------------------------------------------------------
// The same rules as the .xshd, in the same precedence: comment and string spans first, then the four
// sigils, the `?` placeholder, whole-word keywords, numbers. Anything else is plain text.

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const VEIN_TOKEN = new RegExp([
  /(\/\/[^\n]*)/,                          // 1 line comment
  /(\/\*[\s\S]*?(?:\*\/|$))/,              // 2 block comment
  /("(?:\\.|[^"\\\n])*"?)/,                // 3 string (an unterminated one stops at the line end)
  /(\$[A-Za-z_][A-Za-z0-9_]*)/,            // 4 $Shape
  /(#[A-Za-z_][A-Za-z0-9_]*)/,             // 5 #Mark
  /(@[A-Za-z_][A-Za-z0-9_]*)/,             // 6 @Event
  /(&[A-Za-z_][A-Za-z0-9_]*)/,             // 7 &Builder
  /(\?)/,                                  // 8 fill-the-rest
  /([A-Za-z_][A-Za-z0-9_]*)/,              // 9 word — a keyword or plain
  /(\d+(?:\.\d+)?%?)/,                     // 10 number
].map((r) => r.source).join('|'), 'g');

const CLASS = { 1: 'c', 2: 'c', 3: 's', 4: 'sh', 5: 'mk', 6: 'ev', 7: 'bd', 8: 'sg', 10: 'n' };

function highlightVein(src) {
  let out = '', last = 0, m;
  VEIN_TOKEN.lastIndex = 0;
  while ((m = VEIN_TOKEN.exec(src))) {
    if (m[0] === '') { VEIN_TOKEN.lastIndex++; continue; }
    out += esc(src.slice(last, m.index));
    const g = m.slice(1).findIndex((x) => x !== undefined) + 1;
    const cls = g === 9 ? (KEYWORDS.has(m[0]) ? 'k' : null) : CLASS[g];
    out += cls ? `<span class="${cls}">${esc(m[0])}</span>` : esc(m[0]);
    last = m.index + m[0].length;
  }
  return out + esc(src.slice(last));
}

// vein.discovery is its own tiny format: `#` starts a COMMENT there, not a mark.
function highlightDiscovery(src) {
  return src.split('\n').map((line) => {
    const hash = line.indexOf('#');
    const code = hash < 0 ? line : line.slice(0, hash);
    const note = hash < 0 ? '' : line.slice(hash);
    const c = esc(code).replace(/\b(silent|expose|all)\b/g, '<span class="k">$1</span>');
    return c + (note ? `<span class="c">${esc(note)}</span>` : '');
  }).join('\n');
}

const langOf = (file) => (file.endsWith('.discovery') ? 'discovery' : 'vein');
const highlight = (src, lang) => (lang === 'discovery' ? highlightDiscovery(src) : lang === 'vein' ? highlightVein(src) : esc(src));

// ---- the sample files, discovered rather than listed ---------------------------------------------

function walk(dir) {
  const out = [];
  for (const e of fs.readdirSync(abs(dir), { withFileTypes: true })) {
    const p = dir + '/' + e.name;
    if (e.isDirectory()) out.push(...walk(p));
    else if (/\.(vein|discovery)$/.test(e.name)) out.push(p);
  }
  return out.sort();
}
const ON_DISK = walk('samples');
const EXCLUDED = cur.excluded || {};

// Every sample entry (a file, or a folder standing for a multi-file project) and the files it owns.
const SAMPLES = cur.samples || {};
const owned = new Map();                      // file -> sample key
for (const [key, s] of Object.entries(SAMPLES)) {
  const files = s.files && s.files.length ? s.files : [key];
  s.files = files;
  for (const f of files) {
    if (!fs.existsSync(abs(f))) errors.push(`samples["${key}"] lists ${f}, which does not exist`);
    owned.set(f, key);
  }
}
for (const f of ON_DISK)
  if (!owned.has(f) && !EXCLUDED[f]) errors.push(`${f} is a sample on disk the curriculum never mentions — add it to "samples" (or "excluded", with the reason)`);

// ---- excerpts: cut by anchor, never copied -------------------------------------------------------

function excerpt(x, where) {
  if (!fs.existsSync(abs(x.file))) { errors.push(`${where}: ${x.file} does not exist`); return null; }
  const lines = read(x.file).split('\n');
  const hits = [];
  lines.forEach((l, i) => { if (l.trim() === x.anchor.trim()) hits.push(i); });
  if (hits.length !== 1) {
    errors.push(`${where}: anchor ${JSON.stringify(x.anchor)} matches ${hits.length} lines of ${x.file} (needs exactly 1)`);
    return null;
  }
  const start = hits[0];
  let body = lines.slice(start, start + x.lines);
  // Strip the common indent, so an excerpt from deep inside a bundle starts at the left edge on screen.
  const pad = Math.min(...body.filter((l) => l.trim()).map((l) => l.match(/^ */)[0].length));
  body = body.map((l) => l.slice(pad)).join('\n').replace(/\s+$/, '');
  return { file: x.file, from: start + 1, to: start + body.split('\n').length, code: body, caption: x.caption || x.why || '' };
}

const LESSONS = cur.lessons || [];
for (const l of LESSONS) {
  l.cuts = (l.show || []).map((x, i) => excerpt(x, `lesson "${l.id}" show[${i}]`)).filter(Boolean);
  for (const s of l.samples || []) if (!SAMPLES[s]) errors.push(`lesson "${l.id}" names sample ${s}, which has no entry in "samples"`);
}
for (const [key, s] of Object.entries(SAMPLES))
  if (s.excerpt) s.cut = excerpt(s.excerpt, `samples["${key}"].excerpt`);

if (errors.length) {
  console.error(`gen-video-guide: ${errors.length} problem(s), nothing written:\n  - ` + errors.join('\n  - '));
  process.exit(1);
}

// Which lessons use a sample — so the catalog can point back into the curriculum.
const usedBy = {};
for (const l of LESSONS) for (const s of l.samples || []) (usedBy[s] ||= []).push(l);

const lessonNo = new Map(LESSONS.map((l, i) => [l.id, i + 1]));
const parts = [];
for (const l of LESSONS) {
  if (!parts.length || parts[parts.length - 1].title !== l.part) parts.push({ title: l.part, lessons: [] });
  parts[parts.length - 1].lessons.push(l);
}
const totalMin = LESSONS.reduce((a, l) => a + (l.minutes || 0), 0);
const coreMin = LESSONS.filter((l) => l.core).reduce((a, l) => a + (l.minutes || 0), 0);

// =================================================================================================
//  MARKDOWN — for Claude. Plain, dense, every code block fenced as `vein`.
// =================================================================================================

const fence = (code, lang = 'vein') => {
  const ticks = code.includes('```') ? '````' : '```';
  return `${ticks}${lang}\n${code}\n${ticks}`;
};
const bullets = (xs) => (xs || []).map((x) => `- ${x}`).join('\n');

function mdSampleMeta(key, s) {
  const out = [];
  out.push(`**${s.title}** — ${s.summary}`);
  if (s.files.length > 1) out.push(`Files: ${s.files.map((f) => '`' + f + '`').join(', ')}`);
  if (s.run) out.push(`Run: \`${s.run}\`${s.interactive ? ' _(interactive — needs input, a second process, a network or a browser)_' : ''}`);
  out.push(`Teaches: ${(s.teaches || []).join(' · ')}`);
  out.push(`Difficulty ${s.difficulty}/5 · video value: ${s.video_value}${usedBy[key] ? ' · used in lesson ' + usedBy[key].map((l) => lessonNo.get(l.id)).join(', ') : ''}`);
  if (s.notes) out.push(`Notes: ${s.notes}`);
  return out.join('  \n');
}

function buildGuideMd() {
  const o = [];
  o.push(`# VeinScript — video guide`);
  o.push(`> Generated by \`tools/gen-video-guide.js\` from \`${CURRICULUM}\` and the live files in \`samples/\`. Do not edit by hand: every code block below is cut from a real sample, so it compiles.`);
  o.push(`## Read this first (for Claude)`);
  o.push(cur.forClaude);
  o.push(`## The pitch`);
  o.push(cur.pitch);
  o.push(`## The mental model`);
  o.push(cur.mentalModel);
  o.push(`## The four sigils`);
  o.push(`| sigil | names | example | means |\n|---|---|---|---|\n` +
    cur.sigils.map((s) => `| \`${s.sigil}\` | ${s.kind} | \`${s.example}\` | ${s.meaning} |`).join('\n'));
  o.push(`## The video plan`);
  o.push(`${LESSONS.length} lessons, about ${totalMin} minutes in full. The **core path** (lessons marked ★) is about ${coreMin} minutes and is a complete introduction on its own.`);
  for (const p of parts) {
    o.push(`**${p.title}**\n` + p.lessons.map((l) => `${lessonNo.get(l.id)}. ${l.core ? '★ ' : ''}${l.title} (${l.minutes} min) — ${l.goal}`).join('\n'));
  }
  for (const p of parts) {
    o.push(`---\n\n# Part: ${p.title}`);
    for (const l of p.lessons) {
      const n = lessonNo.get(l.id);
      o.push(`## Lesson ${n}. ${l.title}${l.core ? ' ★' : ''}`);
      o.push(`**Goal:** ${l.goal}  \n**Length:** about ${l.minutes} min  \n**Samples:** ${(l.samples || []).map((s) => '`' + s + '`').join(', ')}`);
      if (l.hook) o.push(`**Open with:** ${l.hook}`);
      o.push(`### Say`);
      o.push(bullets(l.say));
      for (const c of l.cuts) {
        o.push(`### Show — \`${c.file}\` lines ${c.from}–${c.to}${c.caption ? '\n' + c.caption : ''}`);
        o.push(fence(c.code));
      }
      if (l.run) {
        const out = l.output ?? SAMPLES[l.run.sample]?.output;
        o.push(`### Run\n\`${l.run.cmd}\``);
        if (out) o.push(`Real output:\n` + fence(out, 'text'));
      }
      if (l.gotchas && l.gotchas.length) { o.push(`### Watch out`); o.push(bullets(l.gotchas)); }
      if (l.next) o.push(`**Into the next lesson:** ${l.next}`);
    }
  }
  o.push(`---\n\n# Reference`);
  o.push(`## The rules that bite (docs/RULES.md)`);
  o.push(cur.rules.map((r) => {
    let s = `- **${r.number}.** ${r.rule}${r.beginner ? ' _(first-hour)_' : ''}`;
    if (r.example_wrong) s += `\n  - wrong: \`${r.example_wrong.replace(/\n/g, ' ⏎ ')}\``;
    if (r.example_right) s += `\n  - right: \`${r.example_right.replace(/\n/g, ' ⏎ ')}\``;
    return s;
  }).join('\n'));
  o.push(`## What VeinScript deliberately does not have (docs/SYNTAX-DECISIONS.md)`);
  o.push(`| id | not in VeinScript | write instead | why |\n|---|---|---|---|\n` +
    cur.refusals.map((r) => `| ${r.id} | ${r.missing} | ${r.instead} | ${r.why} |`).join('\n'));
  o.push(`## Glossary`);
  o.push(`| term | syntax | meaning | first seen in |\n|---|---|---|---|\n` +
    cur.glossary.map((g) => `| ${g.term} | \`${g.syntax}\` | ${g.meaning} | \`${g.first_sample}\` |`).join('\n'));
  o.push(`## The toolchain`);
  o.push(`| command | what it does |\n|---|---|\n` + cur.toolchain.map((t) => `| \`${t.command}\` | ${t.what} |`).join('\n'));
  if (cur.workbench) { o.push(`## The Workbench (the IDE)`); o.push(cur.workbench); }
  if (cur.unimplemented?.length) { o.push(`## Reserved but not implemented`); o.push(bullets(cur.unimplemented)); }
  o.push(`## Every sample, one line each`);
  o.push(`Full source for all of them is in \`ALL-SAMPLES.md\`.\n\n| sample | what it is | teaches | value |\n|---|---|---|---|\n` +
    Object.entries(SAMPLES).map(([k, s]) => `| \`${k}\` | ${s.title} | ${(s.teaches || []).slice(0, 4).join(', ')} | ${s.video_value} |`).join('\n'));
  return o.join('\n\n') + '\n';
}

function buildAllMd() {
  const o = [];
  o.push(`# VeinScript — every sample, full source`);
  o.push(`> Generated by \`tools/gen-video-guide.js\`. The companion to \`VIDEO-GUIDE.md\`: that file is the lesson plan, this one is the complete code of every sample in \`samples/\`, verbatim, each with what it teaches. When the two disagree about syntax, **this file is right** — it is the code the compiler accepts.`);
  if (Object.keys(EXCLUDED).length)
    o.push(`Not included:\n` + Object.entries(EXCLUDED).map(([f, why]) => `- \`${f}\` — ${why}`).join('\n'));
  for (const g of groupSamples()) {
    o.push(`---\n\n# ${g.title}`);
    for (const [key, s] of g.items) {
      o.push(`## \`${key}\``);
      o.push(mdSampleMeta(key, s));
      if (s.output) o.push(`Real output of \`${s.run}\`:\n` + fence(s.output, 'text'));
      for (const f of s.files) {
        if (s.files.length > 1) o.push(`### \`${f}\``);
        o.push(fence(read(f).replace(/\s+$/, ''), langOf(f) === 'discovery' ? 'text' : 'vein'));
      }
    }
  }
  return o.join('\n\n') + '\n';
}

function groupSamples() {
  const groups = new Map();
  for (const [key, s] of Object.entries(SAMPLES)) {
    const g = s.group || 'Other';
    if (!groups.has(g)) groups.set(g, []);
    groups.get(g).push([key, s]);
  }
  const order = cur.sampleGroups || [];
  return [...groups.entries()]
    .sort((a, b) => (order.indexOf(a[0]) + 1 || 99) - (order.indexOf(b[0]) + 1 || 99))
    .map(([title, items]) => ({ title, items }));
}

// =================================================================================================
//  HTML — for people. Same content, coloured like the Workbench.
// =================================================================================================

// A small markdown reader for the curriculum's prose fields: paragraphs, lists, **bold**, `code`
// (run through the Vein highlighter, so `$Health` in a sentence is teal like it is in the editor),
// fenced blocks and pipe tables. Enough for what the JSON holds; not a general markdown.
function inline(s) {
  return esc(s)
    .replace(/`([^`]+)`/g, (_, c) => `<code class="ic">${highlightVein(c.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&'))}</code>`)
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*\w])\*([^*\s][^*]*?)\*(?![*\w])/g, '$1<em>$2</em>')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1');
}

function md(src) {
  if (!src) return '';
  const lines = src.replace(/\r\n/g, '\n').split('\n');
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (/^```/.test(line)) {
      const lang = line.slice(3).trim() || 'vein';
      const body = [];
      i++;
      while (i < lines.length && !/^```/.test(lines[i])) body.push(lines[i++]);
      i++;
      out.push(codeBlock(body.join('\n'), { lang: lang === 'text' ? 'text' : 'vein' }));
      continue;
    }
    if (/^#{1,4} /.test(line)) { const h = line.match(/^(#+) (.*)/); out.push(`<h${h[1].length + 2}>${inline(h[2])}</h${h[1].length + 2}>`); i++; continue; }
    if (/^\s*[-*] /.test(line)) {
      const items = [];
      while (i < lines.length && /^\s*[-*] /.test(lines[i])) items.push(lines[i++].replace(/^\s*[-*] /, ''));
      out.push('<ul>' + items.map((x) => `<li>${inline(x)}</li>`).join('') + '</ul>');
      continue;
    }
    if (/^\s*\d+\. /.test(line)) {
      const items = [];
      while (i < lines.length && /^\s*\d+\. /.test(lines[i])) items.push(lines[i++].replace(/^\s*\d+\. /, ''));
      out.push('<ol>' + items.map((x) => `<li>${inline(x)}</li>`).join('') + '</ol>');
      continue;
    }
    if (/^\|/.test(line)) {
      const rows = [];
      while (i < lines.length && /^\|/.test(lines[i])) rows.push(lines[i++]);
      const cells = (r) => r.replace(/^\||\|$/g, '').split('|').map((c) => c.trim());
      const [head, , ...body] = rows;
      out.push(`<div class="tbl"><table><thead><tr>${cells(head).map((c) => `<th>${inline(c)}</th>`).join('')}</tr></thead><tbody>` +
        body.map((r) => `<tr>${cells(r).map((c) => `<td>${inline(c)}</td>`).join('')}</tr>`).join('') + '</tbody></table></div>');
      continue;
    }
    if (!line.trim()) { i++; continue; }
    const para = [];
    while (i < lines.length && lines[i].trim() && !/^(```|#{1,4} |\s*[-*] |\s*\d+\. |\|)/.test(lines[i])) para.push(lines[i++]);
    out.push(`<p>${inline(para.join(' '))}</p>`);
  }
  return out.join('\n');
}

let blockId = 0;
function codeBlock(code, { lang = 'vein', file = '', from = 1, numbers = false, label = '' } = {}) {
  const id = 'cb' + (++blockId);
  const body = highlight(code, lang);
  const gutter = numbers
    ? `<span class="gut" aria-hidden="true">${code.split('\n').map((_, i) => from + i).join('\n')}</span>`
    : '';
  const head = file || label
    ? `<div class="cbh"><span class="cbf">${esc(file || label)}${file && numbers ? `<span class="cbl">:${from}</span>` : ''}</span><button class="copy" data-for="${id}" type="button">Copy</button></div>`
    : `<div class="cbh cbh-min"><button class="copy" data-for="${id}" type="button">Copy</button></div>`;
  return `<figure class="cb ${lang === 'text' ? 'term' : ''}">${head}<div class="cbs"><pre>${gutter}<code id="${id}">${body}</code></pre></div></figure>`;
}

function runBlock(cmd, output) {
  const id = 'cb' + (++blockId);
  return `<figure class="cb term"><div class="cbh"><span class="cbf">terminal</span><button class="copy" data-for="${id}" type="button">Copy command</button></div>` +
    `<div class="cbs"><pre><code><span class="prompt">&gt;</span> <span id="${id}">${esc(cmd)}</span>${output ? '\n' + `<span class="out">${esc(output)}</span>` : ''}</code></pre></div></figure>`;
}

// The side nav is too small for boxed inline code: a `run` there becomes plain monospace.
const navText = (s) => esc(s).replace(/`([^`]+)`/g, '<span class="navc">$1</span>');
const chip = (t, cls = '') => `<span class="chip ${cls}">${inline(t)}</span>`;
const slug = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

function sampleCard(key, s, open = false) {
  const files = s.files.map((f) => codeBlock(read(f).replace(/\s+$/, ''), { lang: langOf(f), file: f, numbers: true })).join('');
  const lessons = usedBy[key] ? usedBy[key].map((l) => `<a href="#${l.id}">lesson ${lessonNo.get(l.id)}</a>`).join(', ') : '';
  const lines = s.files.reduce((a, f) => a + read(f).split('\n').length, 0);
  return `<details class="sample" id="s-${slug(key)}" data-search="${esc([key, s.title, s.summary, ...(s.teaches || []), ...(s.keywords || [])].join(' ').toLowerCase())}"${open ? ' open' : ''}>
  <summary><span class="sname">${esc(key)}</span><span class="stitle">${esc(s.title)}</span><span class="val val-${s.video_value}">${s.video_value}</span></summary>
  <div class="sbody">
    <p>${inline(s.summary)}</p>
    <dl class="meta">
      <div><dt>Teaches</dt><dd>${(s.teaches || []).map((t) => chip(t)).join(' ')}</dd></div>
      ${s.prerequisites?.length ? `<div><dt>Needs first</dt><dd>${s.prerequisites.map((t) => chip(t, 'dim')).join(' ')}</dd></div>` : ''}
      <div><dt>Level</dt><dd>${'●'.repeat(s.difficulty)}${'○'.repeat(5 - s.difficulty)} <span class="muted">· ${lines} lines${lessons ? ' · in ' + lessons : ''}</span></dd></div>
    </dl>
    ${s.notes ? `<p class="note">${inline(s.notes)}</p>` : ''}
    ${s.run ? runBlock(s.run, s.output) : ''}
    ${s.interactive ? '<p class="muted small">Interactive — it needs typing, a second process, a network or a browser, so the output above is not captured.</p>' : ''}
    ${files}
  </div>
</details>`;
}

function buildHtml() {
  const c = colors;
  const nav = parts.map((p) => `<div class="navpart"><div class="navh">${esc(p.title)}</div>` +
    p.lessons.map((l) => `<a href="#${l.id}"><span class="navn">${lessonNo.get(l.id)}</span><span>${navText(l.title)}</span>${l.core ? '<span class="star" title="core path">★</span>' : ''}</a>`).join('') + '</div>').join('');

  const lessonsHtml = parts.map((p) => `<section class="part"><h2 class="parth" id="part-${slug(p.title)}">${esc(p.title)}</h2>` + p.lessons.map((l) => {
    const n = lessonNo.get(l.id);
    const out = l.output ?? (l.run ? SAMPLES[l.run.sample]?.output : '');
    return `<article class="lesson" id="${l.id}">
  <header class="lh">
    <div class="lnum">${n}</div>
    <div>
      <h3>${inline(l.title)}${l.core ? ' <span class="star" title="core path">★</span>' : ''}</h3>
      <p class="goal">${inline(l.goal)}</p>
      <p class="lmeta">~${l.minutes} min · ${(l.samples || []).map((s) => `<a href="#s-${slug(s)}">${esc(s.replace(/^samples\//, ''))}</a>`).join(' · ')}</p>
    </div>
  </header>
  ${l.hook ? `<p class="hook"><span class="lab">Open with</span>${inline(l.hook)}</p>` : ''}
  <div class="say"><div class="lab">Say</div><ul>${(l.say || []).map((x) => `<li>${inline(x)}</li>`).join('')}</ul></div>
  ${l.cuts.map((x) => `${x.caption ? `<p class="cap">${inline(x.caption)}</p>` : ''}${codeBlock(x.code, { file: x.file, from: x.from, numbers: true })}`).join('')}
  ${l.run ? `<div class="lab">Run it</div>${runBlock(l.run.cmd, out)}` : ''}
  ${l.gotchas?.length ? `<aside class="gotcha"><div class="lab">Watch out</div><ul>${l.gotchas.map((x) => `<li>${inline(x)}</li>`).join('')}</ul></aside>` : ''}
  ${l.next ? `<p class="next">${inline(l.next)}</p>` : ''}
</article>`;
  }).join('') + '</section>').join('');

  const sigils = cur.sigils.map((s) => `<div class="sig"><code class="ic big">${highlightVein(s.example)}</code><div><strong>${esc(s.kind)}</strong><p class="sigm">${inline(s.meaning)}</p></div></div>`).join('');

  const rules = cur.rules.map((r) => `<li class="${r.beginner ? 'beg' : ''}"><span class="rn">${esc(r.number)}</span><div>${inline(r.rule)}${r.beginner ? ' <span class="chip hot">first hour</span>' : ''}` +
    (r.example_wrong || r.example_right ? `<div class="wr">${r.example_wrong ? `<div><span class="x">wrong</span>${codeBlock(r.example_wrong)}</div>` : ''}${r.example_right ? `<div><span class="ok">right</span>${codeBlock(r.example_right)}</div>` : ''}</div>` : '') +
    `</div></li>`).join('');

  const catalog = groupSamples().map((g) => `<h3 class="grp">${esc(g.title)} <span class="muted">${g.items.length}</span></h3>` + g.items.map(([k, s]) => sampleCard(k, s)).join('')).join('');

  return `<title>VeinScript Video Guide</title>
<meta name="description" content="${esc(cur.pitch.slice(0, 180))}">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,500;12..96,700&family=Instrument+Sans:ital,wght@0,400;0,600;1,400&family=JetBrains+Mono:wght@400;600&display=swap">
<style>
/* One look on purpose: this page is the Workbench's companion, and the video shows the Workbench. The
   palette is src/Vein.Workbench/Theme.axaml; the syntax colours are VeinScript.xshd, read at build time. */
:root {
  color-scheme: dark;
  --ground: #12141A; --panel: #191C23; --header: #15181E; --sunken: #0E1015; --raised: #232830;
  --line: #282D36; --accent: #2F6FED; --accent-hi: #4A84F5; --ink: #E6E9F0; --muted: #9AA2B2;
  --good: #3BA55D; --bad: #E0575B;
  --code-ink: #D6DAE3;
  --t-comment: ${c.Comment}; --t-string: ${c.String}; --t-number: ${c.Number}; --t-keyword: ${c.Keyword};
  --t-shape: ${c.Shape}; --t-mark: ${c.Mark}; --t-event: ${c.Event}; --t-builder: ${c.Builder}; --t-sigil: ${c.Sigil};
  --display: "Bricolage Grotesque", "Segoe UI", system-ui, sans-serif;
  --body: "Instrument Sans", "Segoe UI", system-ui, sans-serif;
  --mono: "Cascadia Code", "JetBrains Mono", Consolas, Menlo, monospace;
}
* { box-sizing: border-box; }
html { scroll-behavior: smooth; scroll-padding-top: 72px; }
@media (prefers-reduced-motion: reduce) { html { scroll-behavior: auto; } }
body { margin: 0; background: var(--ground); color: var(--ink); font: 16px/1.6 var(--body); }
a { color: var(--accent-hi); text-decoration: none; }
a:hover { text-decoration: underline; }
:focus-visible { outline: 2px solid var(--accent-hi); outline-offset: 2px; border-radius: 4px; }
.muted { color: var(--muted); } .small { font-size: 13px; }

/* top bar */
.top { position: sticky; top: env(safe-area-inset-top, 0px); z-index: 10; display: flex; gap: 16px; align-items: center;
  padding: 10px 20px; background: color-mix(in srgb, var(--header) 92%, transparent); backdrop-filter: blur(8px); border-bottom: 1px solid var(--line); }
.brand { font: 700 17px/1 var(--display); letter-spacing: -.01em; white-space: nowrap; }
.brand b { color: var(--accent-hi); font-weight: 700; }
.top input { margin-left: auto; width: min(320px, 50vw); background: var(--sunken); border: 1px solid var(--line); color: var(--ink);
  border-radius: 6px; padding: 7px 10px; font: 14px var(--body); }
.top input:focus { border-color: var(--accent); outline: none; }

/* layout */
.wrap { display: grid; grid-template-columns: 270px minmax(0, 1fr); max-width: 1320px; margin: 0 auto; }
nav.side { position: sticky; top: 53px; align-self: start; height: calc(100vh - 53px); overflow-y: auto; padding: 20px 12px 40px 20px; border-right: 1px solid var(--line); }
.navpart { margin-bottom: 18px; }
.navh { font: 600 11px/1 var(--body); letter-spacing: .09em; text-transform: uppercase; color: var(--muted); margin: 0 0 6px 8px; }
nav.side a { display: flex; gap: 10px; align-items: baseline; padding: 5px 8px; border-radius: 6px; color: var(--ink); font-size: 14px; line-height: 1.35; }
nav.side a:hover { background: var(--raised); text-decoration: none; }
nav.side a.on { background: color-mix(in srgb, var(--accent) 30%, transparent); }
.navc { font: 0.92em var(--mono); }
.navn { font: 600 12px var(--mono); color: var(--muted); min-width: 18px; text-align: right; }
.navextra a { font-size: 13px; color: var(--muted); }
main { padding: 32px 40px 120px; min-width: 0; }
@media (max-width: 900px) {
  .wrap { grid-template-columns: minmax(0, 1fr); }
  nav.side { display: none; }
  main { padding: 24px 16px 96px; }
  .top { padding-inline: 16px; }
}

/* hero */
.hero { max-width: 860px; padding-block: 12px 28px; }
.eyebrow { font: 600 12px/1 var(--mono); color: var(--muted); letter-spacing: .06em; }
h1 { font: 700 clamp(34px, 5vw, 56px)/1.02 var(--display); letter-spacing: -.025em; margin: 10px 0 18px; text-wrap: balance; }
h1 .sh { color: var(--t-shape); } h1 .ev { color: var(--t-event); } h1 .mk { color: var(--t-mark); } h1 .bd { color: var(--t-builder); }
.pitch { font-size: 19px; line-height: 1.55; color: var(--ink); max-width: 68ch; }
.stats { display: flex; flex-wrap: wrap; gap: 8px 22px; margin-top: 18px; font: 13px var(--mono); color: var(--muted); }
.stats b { color: var(--ink); font-weight: 600; }

h2 { font: 700 30px/1.1 var(--display); letter-spacing: -.02em; margin: 56px 0 18px; text-wrap: balance; }
h3 { font: 700 21px/1.2 var(--display); letter-spacing: -.01em; margin: 0; text-wrap: balance; }
h4, h5 { font: 600 16px var(--body); margin: 22px 0 8px; }
.prose { max-width: 72ch; } .prose p, .prose li { color: var(--ink); }
.prose ul, .prose ol { padding-left: 22px; }

/* sigil legend — the colours ARE the lesson */
.sigils { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 10px; margin: 18px 0 8px; max-width: 980px; }
.sig { display: flex; gap: 12px; align-items: flex-start; padding: 14px; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; }
.sig strong { display: block; font-size: 14px; } .sigm { margin: 2px 0 0; font-size: 14px; color: var(--muted); line-height: 1.5; }
.sigm code.ic { white-space: normal; }
code.ic { font: 0.9em var(--mono); background: var(--sunken); color: var(--code-ink); padding: 1px 5px; border-radius: 4px; border: 1px solid var(--line); white-space: nowrap; }
code.ic.big { font-size: 16px; padding: 4px 8px; }

/* the loop */
.loop { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin: 16px 0; font: 13px var(--mono); }
.loop > span { padding: 6px 10px; border: 1px solid var(--line); background: var(--panel); border-radius: 6px; }
.loop i { color: var(--muted); font-style: normal; }

/* lessons */
.part { margin-top: 12px; }
.parth { padding-top: 22px; border-top: 1px solid var(--line); }
.lesson { max-width: 940px; padding: 28px 0 34px; border-bottom: 1px dashed var(--line); }
.lh { display: grid; grid-template-columns: 52px minmax(0, 1fr); gap: 14px; align-items: start; }
.lnum { font: 700 30px/1 var(--display); color: var(--accent-hi); text-align: right; padding-top: 1px; font-variant-numeric: tabular-nums; }
.goal { margin: 6px 0 2px; color: var(--ink); font-size: 17px; }
.lmeta { margin: 4px 0 0; font: 13px var(--mono); color: var(--muted); }
.lmeta a { color: var(--muted); } .lmeta a:hover { color: var(--accent-hi); }
.star { color: #F2C14E; font-size: .8em; }
.lab { font: 600 11px/1 var(--body); letter-spacing: .09em; text-transform: uppercase; color: var(--muted); margin: 22px 0 8px; }
.hook { margin: 18px 0 0 66px; padding: 12px 16px; background: var(--panel); border-radius: 8px; font-size: 16px; }
.hook .lab { margin: 0 0 6px; }
.say, .lesson > .lab, .lesson > figure, .cap, .gotcha, .next { margin-left: 66px; }
.say ul { margin: 0; padding-left: 20px; } .say li { margin: 4px 0; }
.cap { color: var(--muted); font-size: 14px; margin-top: 18px; margin-bottom: -2px; }
.gotcha { margin-top: 18px; padding: 12px 16px; border-left: 3px solid var(--bad); background: color-mix(in srgb, var(--bad) 8%, var(--ground)); border-radius: 0 8px 8px 0; }
.gotcha .lab { margin-top: 0; color: #F08A8D; } .gotcha ul { margin: 0; padding-left: 20px; }
.next { color: var(--muted); font-style: italic; margin-top: 18px; }
@media (max-width: 640px) { .say, .lesson > .lab, .lesson > figure, .cap, .gotcha, .next, .hook { margin-left: 0; } .lh { grid-template-columns: 36px minmax(0,1fr); } .lnum { font-size: 22px; } }

/* code — an editor pane, in both the lessons and the catalog */
figure.cb { margin: 12px 0 0; background: var(--sunken); border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
.cbh { display: flex; align-items: center; gap: 10px; padding: 6px 8px 6px 12px; background: var(--header); border-bottom: 1px solid var(--line); }
.cbh-min { position: absolute; right: 0; border: 0; background: transparent; }
.cbf { font: 12px var(--mono); color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cbl { color: #6B7385; }
.copy { margin-left: auto; font: 600 12px var(--body); color: var(--muted); background: var(--raised); border: 1px solid var(--line); border-radius: 5px; padding: 3px 9px; cursor: pointer; }
.copy:hover { color: var(--ink); background: #2E343F; }
.cbs { overflow-x: auto; }
.cbh-min + .cbs pre { padding-right: 76px; }
figure.cb { position: relative; }
pre { margin: 0; padding: 14px 16px; font: 14px/1.6 var(--mono); color: var(--code-ink); tab-size: 4; display: flex; }
pre code { white-space: pre; }
.gut { color: #4A5160; text-align: right; padding-right: 16px; margin-right: 16px; border-right: 1px solid var(--line); user-select: none; white-space: pre; min-width: 2.5ch; }
.c { color: var(--t-comment); font-style: italic; } .s { color: var(--t-string); } .n { color: var(--t-number); }
.k { color: var(--t-keyword); font-weight: 600; } .sh { color: var(--t-shape); } .mk { color: var(--t-mark); }
.ev { color: var(--t-event); } .bd { color: var(--t-builder); } .sg { color: var(--t-sigil); }
figure.term pre { color: #C8CDD8; } .prompt { color: var(--good); } .out { color: var(--muted); }

/* reference */
.rules { list-style: none; padding: 0; max-width: 940px; }
.rules li { display: grid; grid-template-columns: 48px minmax(0, 1fr); gap: 12px; padding: 12px 0; border-bottom: 1px solid var(--line); }
.rn { font: 700 15px var(--mono); color: var(--muted); text-align: right; }
.rules li.beg .rn { color: #F2C14E; }
.wr { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 10px; margin-top: 6px; }
.wr .x, .wr .ok { font: 600 11px var(--body); letter-spacing: .08em; text-transform: uppercase; }
.wr .x { color: var(--bad); } .wr .ok { color: var(--good); }
.wr figure.cb { margin-top: 4px; }
.tbl { overflow-x: auto; max-width: 1040px; border: 1px solid var(--line); border-radius: 8px; }
table { border-collapse: collapse; width: 100%; font-size: 14px; }
th, td { text-align: left; vertical-align: top; padding: 9px 12px; border-bottom: 1px solid var(--line); }
th { font: 600 12px var(--body); letter-spacing: .06em; text-transform: uppercase; color: var(--muted); background: var(--header); }
tr:last-child td { border-bottom: 0; }

/* catalog */
.grp { margin: 34px 0 10px; font-size: 18px; }
details.sample { max-width: 1040px; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; margin: 8px 0; }
details.sample > summary { display: flex; flex-wrap: wrap; gap: 4px 14px; align-items: baseline; padding: 11px 14px; cursor: pointer; list-style: none; }
details.sample > summary::-webkit-details-marker { display: none; }
details.sample > summary::before { content: "▸"; color: var(--muted); width: 10px; }
details.sample[open] > summary::before { content: "▾"; }
.sname { font: 600 14px var(--mono); }
.stitle { color: var(--muted); font-size: 14px; }
.val { margin-left: auto; font: 600 11px var(--body); letter-spacing: .06em; text-transform: uppercase; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--line); color: var(--muted); }
.val-core { color: #8FB4FF; border-color: #2F4F8F; } .val-good { color: #9FD4AE; border-color: #2D5A3A; }
.sbody { padding: 0 16px 16px 38px; } @media (max-width: 640px) { .sbody { padding-left: 14px; } }
dl.meta { display: grid; gap: 6px; margin: 10px 0; } dl.meta div { display: grid; grid-template-columns: 96px minmax(0,1fr); gap: 10px; }
dt { font: 600 12px var(--body); color: var(--muted); padding-top: 3px; } dd { margin: 0; }
.chip { display: inline-block; font-size: 13px; padding: 1px 8px; margin: 2px 2px 2px 0; border-radius: 999px; background: var(--raised); border: 1px solid var(--line); }
.chip.dim { color: var(--muted); } .chip.hot { color: #F2C14E; border-color: #5A4A1E; font-size: 11px; }
.note { font-size: 14px; color: var(--muted); border-left: 2px solid var(--line); padding-left: 10px; }
.hidden-by-search { display: none; }
footer { margin-top: 80px; color: var(--muted); font-size: 13px; max-width: 72ch; }
</style>

<div class="top">
  <div class="brand">Vein<b>Script</b> · video guide</div>
  <input id="q" type="search" placeholder="Filter samples: folds, hear, #Enemy…" aria-label="Filter samples">
</div>
<div class="wrap">
<nav class="side" aria-label="Lessons">
  ${nav}
  <div class="navpart navextra"><div class="navh">Reference</div>
    <a href="#rules">The rules that bite</a><a href="#refusals">What it doesn't have</a><a href="#glossary">Glossary</a><a href="#toolchain">Toolchain</a><a href="#catalog">Every sample</a></div>
</nav>
<main>
  <section class="hero">
    <div class="eyebrow">IDENTITY ORIENTED PROGRAMMING · ${Object.keys(SAMPLES).length} SAMPLES · ${LESSONS.length} LESSONS</div>
    <h1>Data in <span class="sh">$shapes</span>, messages in <span class="ev">@events</span>, tags in <span class="mk">#marks</span>, templates in <span class="bd">&amp;builders</span>.</h1>
    <p class="pitch">${inline(cur.pitch)}</p>
    <div class="stats"><span><b>${LESSONS.length}</b> lessons</span><span><b>~${totalMin}</b> min in full</span><span><b>~${coreMin}</b> min core path ★</span><span>code cut live from <b>samples/</b></span></div>
  </section>

  <section class="prose" id="model">
    <h2>The mental model</h2>
    ${md(cur.mentalModel)}
    ${cur.loop ? `<div class="loop">${cur.loop.map((x, i) => (i ? '<i>→</i>' : '') + `<span>${inline(x)}</span>`).join('')}</div>` : ''}
  </section>

  <section id="sigils">
    <h2>Four sigils, four colours</h2>
    <p class="prose muted">The Workbench paints each sigil its own colour, and so does every code block on this page. On screen, the colour tells you what kind of name you're looking at before you read it.</p>
    <div class="sigils">${sigils}</div>
  </section>

  ${lessonsHtml}

  <section id="rules"><h2>The rules that bite</h2><p class="prose muted">From docs/RULES.md. The first-hour ones are marked; every one was learned by getting it wrong.</p><ol class="rules">${rules}</ol></section>

  <section id="refusals"><h2>What VeinScript deliberately doesn't have</h2>
    <div class="tbl"><table><thead><tr><th>id</th><th>not here</th><th>write instead</th><th>why</th></tr></thead><tbody>
    ${cur.refusals.map((r) => `<tr><td>${esc(r.id)}</td><td>${inline(r.missing)}</td><td>${inline(r.instead)}</td><td>${inline(r.why)}</td></tr>`).join('')}
    </tbody></table></div></section>

  <section id="glossary"><h2>Glossary</h2>
    <div class="tbl"><table><thead><tr><th>term</th><th>syntax</th><th>meaning</th><th>first seen</th></tr></thead><tbody>
    ${cur.glossary.map((g) => `<tr><td><strong>${esc(g.term)}</strong></td><td><code class="ic">${highlightVein(g.syntax)}</code></td><td>${inline(g.meaning)}</td><td>${SAMPLES[g.first_sample] ? `<a href="#s-${slug(g.first_sample)}">${esc(g.first_sample.replace(/^samples\//, ''))}</a>` : esc(g.first_sample)}</td></tr>`).join('')}
    </tbody></table></div></section>

  <section id="toolchain"><h2>The toolchain</h2>
    <div class="tbl"><table><thead><tr><th>command</th><th>what it does</th></tr></thead><tbody>
    ${cur.toolchain.map((t) => `<tr><td><code class="ic">${esc(t.command)}</code></td><td>${inline(t.what)}</td></tr>`).join('')}
    </tbody></table></div>
    ${cur.workbench ? `<div class="prose"><h3 style="margin-top:28px">The Workbench</h3>${md(cur.workbench)}</div>` : ''}
    ${cur.unimplemented?.length ? `<div class="prose"><h3 style="margin-top:28px">Reserved, not implemented</h3><ul>${cur.unimplemented.map((x) => `<li>${inline(x)}</li>`).join('')}</ul></div>` : ''}
  </section>

  <section id="catalog"><h2>Every sample</h2>
    <p class="prose muted">Full source, coloured like the editor. Use the filter at the top to find every sample that uses a keyword or a concept.</p>
    ${catalog}
  </section>

  <footer>Generated by tools/gen-video-guide.js from ${CURRICULUM} and the live files in samples/. Code blocks are cut from the real samples by anchor, so they are the code the compiler accepts today. Syntax colours are read from ${XSHD}.</footer>
</main>
</div>

<script>
// Copy: the button names its target by id; the clipboard call stays inside the click so it is allowed.
document.addEventListener('click', (e) => {
  const b = e.target.closest('.copy'); if (!b) return;
  const el = document.getElementById(b.dataset.for); if (!el) return;
  const text = el.innerText;
  const done = (t) => { const was = b.textContent; b.textContent = t; setTimeout(() => (b.textContent = was), 1200); };
  const fallback = () => { const r = document.createRange(); r.selectNodeContents(el); const s = getSelection(); s.removeAllRanges(); s.addRange(r); done('Selected'); };
  try { navigator.clipboard.writeText(text).then(() => done('Copied'), fallback); } catch { fallback(); }
});

// Filter the catalog: every word must appear in the sample's name, title, concepts or keywords.
const q = document.getElementById('q');
const cards = [...document.querySelectorAll('details.sample')];
q.addEventListener('input', () => {
  const words = q.value.toLowerCase().split(/\\s+/).filter(Boolean);
  let shown = 0;
  for (const c of cards) {
    const hit = words.every((w) => c.dataset.search.includes(w));
    c.classList.toggle('hidden-by-search', !hit);
    if (words.length) c.open = hit && ++shown <= 3;
  }
  document.querySelectorAll('h3.grp').forEach((h) => {
    let el = h.nextElementSibling, any = false;
    while (el && el.matches('details.sample')) { if (!el.classList.contains('hidden-by-search')) any = true; el = el.nextElementSibling; }
    h.style.display = any ? '' : 'none';
  });
  if (words.length) document.getElementById('catalog').scrollIntoView();
});

// Mark the lesson in view in the side nav.
const links = new Map([...document.querySelectorAll('nav.side a[href^="#"]')].map((a) => [a.getAttribute('href').slice(1), a]));
const io = new IntersectionObserver((es) => {
  for (const e of es) if (e.isIntersecting) { links.forEach((a) => a.classList.remove('on')); links.get(e.target.id)?.classList.add('on'); }
}, { rootMargin: '-20% 0px -70% 0px' });
document.querySelectorAll('article.lesson, section[id]').forEach((s) => links.has(s.id) && io.observe(s));
</script>
`;
}

// ---- write ---------------------------------------------------------------------------------------

fs.mkdirSync(abs('docs/video'), { recursive: true });
const guide = buildGuideMd();
const all = buildAllMd();
const html = buildHtml();
fs.writeFileSync(abs(OUT_GUIDE), guide);
fs.writeFileSync(abs(OUT_ALL), all);
fs.writeFileSync(abs(OUT_HTML), html);

const kb = (s) => (Buffer.byteLength(s) / 1024).toFixed(0) + ' KB';
const tok = (s) => '~' + Math.round(s.length / 3.6 / 1000) + 'k tokens';
console.log(`${LESSONS.length} lessons, ${Object.keys(SAMPLES).length} samples (${ON_DISK.length} files on disk, ${Object.keys(EXCLUDED).length} excluded)`);
console.log(`  ${OUT_GUIDE}  ${kb(guide)}  ${tok(guide)}`);
console.log(`  ${OUT_ALL}  ${kb(all)}  ${tok(all)}`);
console.log(`  ${OUT_HTML}  ${kb(html)}`);
