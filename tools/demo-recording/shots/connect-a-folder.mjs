// Connect a folder: add an SFTP connection to the server compose.demo.yml starts, add a source on it, sync it, then
// search what it synced. Run setup-owner.mjs (or the quickstart shot) first and set DEMO_SFTP_KEY to the server's
// private key. A rerun first removes the source and connection an earlier take made.
import { readFileSync } from 'node:fs';
import { startRecording, signIn, BASE, OWNER } from '../lib.mjs';

const CONNECTION = 'team-share';
const SOURCE = 'payments-runbooks';

const { accessToken } = await (await fetch(`${BASE}/api/v1/auth/token`, {
  method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(OWNER),
})).json();
const api = async (method, url) => {
  const res = await fetch(BASE + url, { method, headers: { Authorization: `Bearer ${accessToken}` } });
  if (!res.ok) throw new Error(`${method} ${url}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
};
const sources = async () => (await api('GET', '/api/sources')).items.filter(s => s.name === SOURCE);
for (const s of await sources()) await api('DELETE', `/api/sources/${s.id}?confirmName=${SOURCE}`);
for (const c of (await api('GET', '/api/v1/connections')).items.filter(c => c.name === CONNECTION)) {
  await api('DELETE', `/api/v1/connections/${c.id}?confirmName=${CONNECTION}`);
}

const state = await signIn(OWNER);
const r = await startRecording('connect-a-folder', { storageState: state });
const { page } = r;

await page.goto(`${BASE}/connections`);
await page.getByRole('button', { name: 'Add connection' }).waitFor();
await page.waitForLoadState('networkidle');
r.mark('connections');
await r.click(page.getByRole('button', { name: 'Add connection' }));
await r.type(page.locator('#connection-name'), CONNECTION, 40);
await r.moveTo(page.locator('select').first());
await page.locator('select').first().selectOption('Sftp');
await r.pause(500);
await r.type(page.getByPlaceholder('files.example.com'), 'demo-sftp', 40);
await r.type(page.getByPlaceholder('connapse', { exact: true }), 'demo', 40);
await r.type(page.getByPlaceholder('/srv/knowledge'), '/shared', 40);
await page.locator('#sftp-key').fill(readFileSync(process.env.DEMO_SFTP_KEY, 'utf8'));
await r.pause(500);
await r.click(page.getByRole('button', { name: 'Create connection' }));
await page.getByText(`Connection '${CONNECTION}' created`).waitFor();
r.mark('connection');
await r.pause(900);

await r.click(page.getByRole('link', { name: 'Add a source' }));
const name = page.locator('#source-name');
await name.waitFor();
r.mark('source-dialog');
await r.type(name, SOURCE, 35);
await r.type(page.locator('#source-description'), 'On-call runbooks from the team share', 20);
await r.click(page.getByRole('button', { name: /^Create/ }).last());
await name.waitFor({ state: 'hidden' });
r.mark('source');

const row = page.locator('tr', { hasText: SOURCE });
await r.pause(600);
await r.click(row.getByRole('button', { name: /Sync/ }).first());
r.mark('sync');
// The page does not refresh itself, so wait on the API, then open Sources again.
for (let i = 0; ; i++) {
  const [s] = await sources();
  if (s?.lastSyncStatus === 'Succeeded') break;
  if (i > 120) throw new Error(`The first sync did not finish: ${JSON.stringify(s)}`);
  await r.pause(1000);
}
r.mark('sync-done');
await r.click(page.getByRole('link', { name: 'Sources' }).first());
const synced = row.locator('.badge', { hasText: 'Synced' });
await synced.waitFor();
r.mark('synced');
await r.moveTo(synced);
await r.pause(1400);

await r.click(page.getByRole('link', { name: 'Search' }).first());
const box = page.locator('input[placeholder="Enter your search query..."]');
await box.waitFor();
await page.waitForLoadState('networkidle');
r.mark('search');
const scope = page.locator('select.form-select').first();
await r.moveTo(scope);
await scope.selectOption({ label: SOURCE });
await r.pause(400);
await r.type(box, 'ledger lock waits are high, what do I do?', 30);
await r.click(page.getByRole('button', { name: 'Search', exact: true }));
await page.locator('.result-card').first().waitFor();
r.mark('results');
await r.moveTo(page.locator('.source-badge').first());
await r.pause(2500);
r.mark('end');
await r.stop();
