// Setup for shot 3 (not recorded): invite Dana and Sam, link them to the example server's users of the same
// name through preset_links, then add the example server as a custom provider, connection and source.
// DEMO_EXAMPLE_DATA must point at a copy of example-data, since preset_links is written into it.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { BASE, OWNER, USERS } from '../lib.mjs';

const SOURCE_TOKEN = 'demo-only-example-token';
const SOURCE_URL = 'http://example-source:8080';

const dataDir = process.env.DEMO_EXAMPLE_DATA;
if (!dataDir) throw new Error('Set DEMO_EXAMPLE_DATA to a copy of tools/demo-recording/example-data.');

const browser = await chromium.launch();
const admin = await browser.newContext();
const page = await admin.newPage();
await page.goto(`${BASE}/login`);
await page.getByRole('textbox', { name: 'Email' }).fill(OWNER.email);
await page.getByRole('textbox', { name: 'Password' }).fill(OWNER.password);
await page.getByRole('button', { name: /sign in|log in/i }).click();
await page.waitForURL(u => !u.pathname.startsWith('/login'));

for (const u of Object.values(USERS)) {
  await page.goto(`${BASE}/admin/users`);
  await page.waitForLoadState('networkidle'); // the Blazor circuit must be up before @bind sees the input
  await page.locator('#inviteEmail').fill(u.email);
  await page.locator('#inviteRole').selectOption('Viewer');
  await page.getByRole('button', { name: 'Send Invite' }).click();
  const link = await page.locator('#inviteLink').inputValue();
  const invitee = await browser.newContext();
  const p = await invitee.newPage();
  await p.goto(link);
  await p.waitForLoadState('networkidle');
  await p.locator('#invite-displayName').fill(u.name);
  await p.locator('#invite-password').fill(u.password);
  await p.locator('#invite-confirmPassword').fill(u.password);
  await p.getByRole('button', { name: 'Create Account' }).click();
  await p.waitForURL(x => !x.pathname.startsWith('/register'));
  await invitee.close();
}

const api = await browser.newContext({ baseURL: BASE });
const login = await (await api.request.post('/api/v1/auth/token', { data: OWNER })).json();
const headers = { Authorization: `Bearer ${login.accessToken}` };
async function call(method, url, data) {
  const res = await api.request.fetch(url, { method, headers, data });
  if (!res.ok()) throw new Error(`${method} ${url}: ${res.status()} ${await res.text()}`);
  return res.status() === 204 ? null : res.json();
}

const users = (await call('GET', '/api/v1/auth/users')).items;
const configFile = path.join(dataDir, 'example.json');
const config = JSON.parse(readFileSync(configFile, 'utf8'));
config.preset_links = Object.fromEntries(Object.entries(USERS).map(([who, u]) => [users.find(x => x.email === u.email).id, who]));
writeFileSync(configFile, JSON.stringify(config, null, 2) + '\n');
execFileSync('docker', ['compose', '--profile', 'demo-source', 'up', '-d', '--force-recreate', 'example-source'], { stdio: 'inherit' });
await new Promise(r => setTimeout(r, 4000));

const provider = await call('POST', '/api/v1/providers/custom', { baseUrl: SOURCE_URL, token: SOURCE_TOKEN, name: 'Halcyon wiki' });
const { connection } = await call('POST', '/api/v1/connections', { name: 'halcyon-wiki', provider: 'Custom', config: { providerId: provider.id } });
const source = await call('POST', '/api/sources', {
  name: 'payments-wiki', connectionId: connection.id, syncIntervalSeconds: 300,
  scope: { type: 'custom', baseUrl: SOURCE_URL, scopeId: 'payments' },
});
await call('POST', `/api/sources/${source.id}/sync`);
console.log({ provider: provider.id, connection: connection.id, source: source.id, links: config.preset_links });
await browser.close();
