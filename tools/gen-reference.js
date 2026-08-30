// tools/gen-reference.js — transcribe the generated VeinScript reference (an HTML export) into the
// `/docs` route of samples/web_app, as a bundle fragment under shards/.
//
//   node tools/gen-reference.js "<path to veinscript-reference.html>"
//
// The output is VERBATIM: no wording, signature or example is altered. The one transformation is
// structural — the source has 88 paragraphs that are literal ```veinscript fences its own generator
// never converted, leaking one line of code per <p>. Those are rebuilt into real code blocks.
//
// Everything the page needs is ordinary Vein.Web / Vein.WebTheme vocabulary: Heading / Subheading /
// Subsubheading, Paragraph, Open("ul") + ListItem + Close("ul"), and CodeBlock.

const fs = require('fs');
const path = require('path');

const SRC = process.argv[2] || 'C:/Users/yo/Downloads/veinscript-reference(6).html';
const OUT = path.join(__dirname, '..', 'samples', 'web_app', 'shards', 'Reference.vein');

const src = fs.readFileSync(SRC, 'utf8');
const body = src.slice(src.indexOf('<main>') + 6, src.indexOf('</main>'));

// ---- helpers -------------------------------------------------------------------------------------

// A Vein string literal. Only \ and " need escaping; the lexer's other escapes (\n \t \r) we emit
// deliberately, never accidentally, so they are added after this point and not by it.
const veinStr = (s) => '"' + s.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';

const stripTags = (s) => s.replace(/<[^>]*>/g, '');

// Wrap prose with a TRAILING `+` — the only continuation VeinScript has. Splitting happens on spaces
// in the RAW text and escaping only afterwards, so a \" can never be cut in half.
function wrap(text, indent, width = 96) {
  const words = text.split(' ');
  const lines = [];
  let cur = '';
  for (const w of words) {
    if (cur && cur.length + 1 + w.length > width) { lines.push(cur); cur = w; }
    else cur = cur ? cur + ' ' + w : w;
  }
  if (cur) lines.push(cur);
  return lines
    .map((l, i) => veinStr(i < lines.length - 1 ? l + ' ' : l))
    .join(' +\n' + ' '.repeat(indent));
}

function emitText(builder, text, indent = 12) {
  const open = ' '.repeat(indent) + 'bring ' + builder + '(';
  return open + wrap(text, open.length) + ')';
}

// A code block: whitespace is the layout, so newlines ride as \n inside the string and each source
// line of the original gets its own source line here.
function emitCode(lines, indent = 12) {
  const open = ' '.repeat(indent) + 'bring CodeBlock(';
  const cont = ' '.repeat(open.length);
  // The \n goes in AFTER escaping, never before: veinStr doubles backslashes, so appending "\n" to the
  // raw line first would emit \\n — which the lexer reads as an escaped backslash plus the letter n,
  // and the block renders as one long line with a visible \n in it.
  const lit = (l, last) => (last ? veinStr(l) : veinStr(l).slice(0, -1) + '\\n"');
  return open + lines
    .map((l, i) => lit(l, i === lines.length - 1))
    .join(' +\n' + cont) + ')';
}

// ---- walk the block sequence ---------------------------------------------------------------------
// The document is a flat run of block elements — no nesting except <li> inside <ul> — which is why a
// regex is enough here and a DOM is not.

const re = /<(h1|h2|h3|p|ul|pre)\b[^>]*>([\s\S]*?)<\/\1>/g;
const nodes = [];
let m;
while ((m = re.exec(body))) nodes.push({ tag: m[1], inner: m[2] });

const out = [];
const stats = { h1: 0, h2: 0, h3: 0, p: 0, ul: 0, li: 0, pre: 0, fenceBlocks: 0, fenceLines: 0 };
let fence = null;

for (const n of nodes) {
  if (n.tag === 'p') {
    const flat = stripTags(n.inner).trim();

    if (/^```/.test(flat)) {                       // a fence marker: open or close a rebuilt block
      if (fence === null) fence = [];
      else { out.push(emitCode(fence.length ? fence : [''])); stats.fenceBlocks++; fence = null; }
      continue;
    }
    if (fence !== null) {                          // inside a fence: it is code, not prose
      // Tags go (an inline-code run inside a fence is just text) but ENTITIES STAY: this lands inside
      // <pre><code>, so a decoded `&lt;` would reopen as a tag and eat the rest of the block.
      fence.push(stripTags(n.inner).replace(/^ {4}/, '').replace(/\s+$/, ''));
      stats.fenceLines++;
      continue;
    }
    if (!flat) continue;
    out.push(emitText('Paragraph', n.inner.trim()));
    stats.p++;
  } else if (n.tag === 'h1') { out.push(emitText('Heading', n.inner.trim())); stats.h1++; }
  else if (n.tag === 'h2') { out.push(emitText('Subheading', n.inner.trim())); stats.h2++; }
  else if (n.tag === 'h3') { out.push(emitText('Subsubheading', n.inner.trim())); stats.h3++; }
  else if (n.tag === 'ul') {
    out.push('            bring Open("ul")');
    for (const li of n.inner.matchAll(/<li>([\s\S]*?)<\/li>/g)) {
      out.push(emitText('ListItem', li[1].trim(), 16));
      stats.li++;
    }
    out.push('            bring Close("ul")');
    stats.ul++;
  } else if (n.tag === 'pre') {
    const code = n.inner.replace(/^\s*<code[^>]*>/, '').replace(/<\/code>\s*$/, '');
    out.push(emitCode(code.split('\n')));
    stats.pre++;
  }
}
if (fence !== null && fence.length) { out.push(emitCode(fence)); stats.fenceBlocks++; }

const header = `// samples/web_app/shards/Reference.vein — the /docs route: the full VeinScript reference.
//
// A FRAGMENT, not a bundle. It carries no \`bundle\` header; BundleLoader merges every .vein under
// shards/ into the bundle in samples/web_app/web_app.vein. That is what keeps the sample readable —
// this is ${nodes.length} content nodes, and inlining it would bury the five routes it documents.
//
// CONTENT IS VERBATIM: a transcription of a generated snapshot dated 2026-08-29. No wording,
// signature or example was changed. Parts of it predate the current language, and the page says so
// in a callout at the top rather than quietly correcting anything.
//
// The one thing that differs is LAYOUT. 88 paragraphs in the source were literal \`\`\`veinscript
// fences its generator never converted, leaking one line of code per <p>; those ${stats.fenceBlocks} blocks are
// rebuilt here as real code blocks. Same text, shown the way it was meant to be.
//
// GENERATED — do not hand-edit. Re-run:  node tools/gen-reference.js <reference.html>

shard Reference {
    hear @Request as r {
        if r.path == "/docs" {
            bring Nav()
            bring Hero("Language Reference", "Every keyword, shape and event, as one page.")
            bring Open("main")
                bring Open("article")

                bring Raw("<div class='callout'>" +
                          "<strong>A snapshot, reproduced unchanged.</strong> Generated 2026-08-29 " +
                          "and transcribed verbatim. Parts have since drifted from the language: " +
                          "several examples close blocks with " +
                          "<code class='inline-code'>end</code>, which is not a keyword &mdash; " +
                          "blocks use braces. The runtime section describes " +
                          "<code class='inline-code'>push</code> buffers, which were removed. " +
                          "<code class='inline-code'>@Request</code> now also carries " +
                          "<code class='inline-code'>method</code> and " +
                          "<code class='inline-code'>body</code>. For what the compiler actually " +
                          "accepts, read stdlib/ and docs/LANGUAGE.md.</div>")

`;

const footer = `
                bring Close("article")
            bring Close("main")
            bring Footer("Transcribed verbatim from the 2026-08-29 reference snapshot")
        }
    }
}
`;

fs.writeFileSync(OUT, header + out.join('\n') + footer, 'utf8');
console.log('wrote ' + OUT);
console.log('source nodes: ' + nodes.length + ', bring statements: ' + out.length);
console.log(stats);
