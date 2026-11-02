// Shots 1, 4 and 6: replays real Claude Code transcripts (from agent-session.mjs) in a terminal-styled page and
// records it. Each tool result line is summarised from that session's actual result, not written by hand.
// Usage: node agent-replay.mjs <video name> <transcript> [<transcript> ...]   (later transcripts are new sessions)
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { startRecording } from '../lib.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const [name, ...sessions] = process.argv.slice(2);
if (!sessions.length) throw new Error('Usage: node agent-replay.mjs <video name> <transcript> [...]');
const transcripts = sessions.map(s => JSON.parse(readFileSync(path.join(here, '../transcripts', `${s}.json`), 'utf8')));

const cut = (s, n) => (s.length > n ? s.slice(0, n - 1) + '…' : s);
const fact = v => cut(v.split(' (')[0], 44); // a remembered value's headline, before its explanation
const day = s => (s ? s.slice(0, 10) : 'now');
const shortSymbol = s => s.split('.').slice(-2).join('.');
const ARGS = ['query', 'symbol', 'subject', 'kind'];

function args(input) {
  return ARGS.filter(k => input[k]).map(k => `${k}: "${cut(String(input[k]), 48)}"`).join(', ');
}

/** One or more short lines that summarise a tool's real result. */
function summarise(tool, text) {
  if (tool === 'search_knowledge') {
    const files = [...new Set([...text.matchAll(/^File: (.+)$/gm)].map(m => m[1]))].slice(0, 2);
    return [`${text.match(/Found \d+ result\(s\)/)?.[0] ?? 'results'} · top: ${files.join(', ')}`];
  }
  const r = JSON.parse(text);
  switch (tool) {
    case 'memory_remember':
    case 'memory_correct': {
      const f = r.remembered;
      return [`saved "${f.attribute}" = ${fact(f.value)} · ${f.status} from ${day(f.validFrom)}`]
        .concat(f.replaces ? [`the earlier fact now ends ${day(f.validFrom)} (kept in history)`] : []);
    }
    case 'memory_recall':
      return r.facts.slice(0, 2).map(f => `${f.status}: "${f.attribute}" = ${fact(f.value)} · since ${day(f.validFrom)}`);
    case 'memory_timeline':
      return r.facts.map(f => `${day(f.validFrom)} → ${f.validTo ? day(f.validTo) : 'now       '}  ${fact(f.value)}`);
    case 'code_find_symbol':
      return r.definitions.map(d => `${d.kind} ${shortSymbol(d.symbol)} · ${d.path}:${d.startLine}`);
    case 'code_impact':
      return [`${r.affected.length} affected:`].concat(r.affected.map(a => `  ${shortSymbol(a.symbol)} · ${a.path.replace(/^\//, '')}`));
    case 'code_references':
      return r.files.map(f => `${f.path} · ${f.reason}`);
    default:
      return [cut(text, 80)];
  }
}

const esc = s => s.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);
const code = s => esc(s).replace(/`([^`]+)`/g, '<span class="code">$1</span>');

const html = `<!doctype html><html><head><style>
  body { margin:0; background:#16161e; color:#c8cbd9; font:17px/1.5 Consolas, 'Cascadia Mono', monospace; }
  .bar { height:34px; background:#1f2030; color:#7a7f99; display:flex; align-items:center; padding:0 18px; font-size:14px; }
  .bar b { color:#c8cbd9; font-weight:normal; margin-right:10px; }
  #term { padding:16px 26px; }
  .prompt { border:1px solid #3b3f58; border-radius:6px; padding:6px 12px; margin-bottom:14px; color:#e6e8f2; }
  .prompt::before { content:'> '; color:#8b5cf6; }
  .tool { margin-top:8px; } .tool .dot { color:#22c55e; } .tool .name { color:#e6e8f2; font-weight:bold; }
  .tool .args { color:#9aa0bd; }
  .res { color:#9aa0bd; white-space:pre; } .res::before { content:'  ⎿  '; color:#565b78; }
  .res.more::before { color:transparent; }
  .answer { margin-top:14px; color:#e6e8f2; white-space:pre-wrap; } .answer .dot { color:#c8cbd9; }
  .code { color:#f0abfc; }
  .divider { color:#565b78; text-align:center; margin:4px 0 14px; }
  .caption { position:fixed; right:18px; bottom:12px; font:13px system-ui, sans-serif; color:#7a7f99; }
  .hidden { display:none; }
</style></head><body>
  <div class="bar"><b>claude</b>MCP: connapse · http://localhost:5201/mcp</div>
  <div id="term"></div>
  <div class="caption">Replayed from a real Claude Code session · demo data</div>
</body></html>`;

const r = await startRecording(name);
const { page } = r;
await page.setContent(html);
const add = (cls, inner) => page.evaluate(([c, h]) => {
  const d = document.createElement('div'); d.className = c; d.innerHTML = h;
  document.getElementById('term').appendChild(d);
}, [cls, inner]);

await r.pause(500);
for (const [i, t] of transcripts.entries()) {
  if (i > 0) {
    await page.evaluate(() => { document.getElementById('term').innerHTML = ''; });
    await add('divider', '── new session: nothing carried over but Connapse ──');
    await r.pause(900);
  }
  r.mark(`session${i}`);
  await add('prompt', '');
  for (const ch of t.prompt) {
    await page.evaluate(c => { [...document.querySelectorAll('.prompt')].at(-1).textContent += c; }, ch);
    await r.pause(14);
  }
  r.mark(`typed${i}`);
  await r.pause(500);
  for (const s of t.steps) {
    await add('tool', `<span class="dot">● </span><span class="name">connapse · ${s.tool}</span><span class="args">(${esc(args(s.input))})</span>`);
    await r.pause(700);
    for (const [j, line] of summarise(s.tool, s.result).entries()) await add(j ? 'res more' : 'res', esc(line));
    await r.pause(700);
  }
  await add('answer', `<span class="dot">● </span>${code(t.answer)}`);
  r.mark(`answer${i}`);
  await r.pause(i === transcripts.length - 1 ? 3500 : 2500);
}
r.mark('end');
await r.stop();
