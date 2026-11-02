// Runs one real headless Claude Code session against the demo server's MCP endpoint and saves a trimmed
// transcript (prompt, tool calls and results, answer) to transcripts/<name>.json for agent-replay.mjs.
// Usage: node agent-session.mjs <name> "<prompt>"            (needs DEMO_AGENT_KEY_FILE, from setup-agent.mjs)
//        node agent-session.mjs <name> "<prompt>" --from <stream.jsonl>   (re-trim a saved session)
// The raw stream-json goes to RAW/<name>.jsonl; its first line holds local paths, so it never enters the repo.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { BASE, RAW } from '../lib.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const [name, prompt, flag, from] = process.argv.slice(2);
if (!name || !prompt) throw new Error('Usage: node agent-session.mjs <name> "<prompt>" [--from <stream.jsonl>]');

let stream;
if (flag === '--from') {
  stream = readFileSync(from, 'utf8');
} else {
  const keyFile = process.env.DEMO_AGENT_KEY_FILE;
  if (!keyFile) throw new Error('Set DEMO_AGENT_KEY_FILE to the key file setup-agent.mjs wrote.');
  mkdirSync(RAW, { recursive: true });
  const mcpConfig = path.join(RAW, 'mcp.json');
  writeFileSync(mcpConfig, JSON.stringify({ mcpServers: { connapse: {
    type: 'http', url: `${BASE}/mcp`, headers: { 'X-Api-Key': readFileSync(keyFile, 'utf8').trim() },
  } } }));
  // Only the Connapse tools: no shell or file access, and no user or project settings.
  const r = spawnSync('claude', ['-p', '--output-format', 'stream-json', '--verbose', '--mcp-config', mcpConfig,
    '--strict-mcp-config', '--setting-sources', 'local', '--allowedTools', 'mcp__connapse',
    '--disallowedTools', 'Bash,Read,Write,Edit,Glob,Grep,WebFetch,WebSearch,Task,Agent,NotebookEdit,Skill',
    '--no-session-persistence', '--model', 'sonnet'],
  { cwd: RAW, encoding: 'utf8', input: prompt, maxBuffer: 64e6, shell: true });
  if (r.status !== 0) throw new Error(`claude exited ${r.status}: ${r.stderr}`);
  stream = r.stdout;
  writeFileSync(path.join(RAW, `${name}.jsonl`), stream);
}

const steps = [];
let answer = '';
for (const line of stream.split('\n').filter(Boolean)) {
  const m = JSON.parse(line);
  for (const c of (m.type === 'assistant' || m.type === 'user') ? m.message.content ?? [] : []) {
    if (c.type === 'tool_use' && c.name.startsWith('mcp__connapse__')) {
      steps.push({ id: c.id, tool: c.name.slice('mcp__connapse__'.length), input: c.input });
    } else if (c.type === 'tool_result') {
      const step = steps.find(s => s.id === c.tool_use_id);
      if (step) step.result = [].concat(c.content).map(x => x.text ?? '').join('');
    } else if (c.type === 'text' && m.type === 'assistant') {
      answer = c.text;
    }
  }
}
const out = path.join(here, '../transcripts', `${name}.json`);
mkdirSync(path.dirname(out), { recursive: true });
writeFileSync(out, JSON.stringify({ prompt, steps: steps.map(({ id, ...s }) => s), answer }, null, 2) + '\n');
console.log(`${out}: ${steps.map(s => s.tool).join(', ')}`);
