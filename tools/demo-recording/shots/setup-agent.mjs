// Setup for the agent shots (not recorded): upload the Halcyon notes to the `halcyon` container (made in shot 2),
// put the example source server's C# code and tests in an `example-server` container, and create an agent key.
// The key is written to the file named by DEMO_AGENT_KEY_FILE and never printed.
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { BASE, OWNER } from '../lib.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../..');
const keyFile = process.env.DEMO_AGENT_KEY_FILE;
if (!keyFile) throw new Error('Set DEMO_AGENT_KEY_FILE to a path outside the repository.');

const { accessToken } = await (await fetch(`${BASE}/api/v1/auth/token`, {
  method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(OWNER),
})).json();
async function call(method, url, body) {
  const json = body && !(body instanceof FormData);
  const res = await fetch(BASE + url, {
    method, body: json ? JSON.stringify(body) : body,
    headers: { Authorization: `Bearer ${accessToken}`, ...(json ? { 'content-type': 'application/json' } : {}) },
  });
  if (!res.ok) throw new Error(`${method} ${url}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}
async function upload(containerId, folder, dir, files) {
  const form = new FormData();
  for (const f of files) form.append('files', new Blob([readFileSync(path.join(dir, f))]), f);
  form.append('path', folder);
  await call('POST', `/api/containers/${containerId}/files`, form);
}
const cs = dir => readdirSync(dir).filter(f => f.endsWith('.cs'));

const containers = (await call('GET', '/api/containers')).items;
const halcyon = containers.find(c => c.name === 'halcyon');
const corpus = path.join(here, '../corpus');
await upload(halcyon.id, '/', corpus, readdirSync(corpus).filter(f => f.endsWith('.md')));

const code = await call('POST', '/api/containers', { name: 'example-server', description: 'Example custom source server (C#)' });
const server = path.join(repo, 'samples/Connapse.CustomSource.Example');
const tests = path.join(repo, 'tests/Connapse.CustomSource.Example.Tests');
await upload(code.id, '/src', server, cs(server));
await upload(code.id, '/tests', tests, cs(tests));

const agent = await call('POST', '/api/v1/agents', { name: 'claude-code', description: 'Claude Code for the demo recordings' });
const key = await call('POST', `/api/v1/agents/${agent.id}/keys`, { name: 'demo', scopes: ['knowledge:read', 'knowledge:write'] });
writeFileSync(keyFile, key.token);
console.log({ halcyon: halcyon.id, code: code.id, agent: agent.id });
