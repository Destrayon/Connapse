// Connectors: the Providers page lists the clouds and sites Connapse reads from, then a custom source server (the
// example server compose.demo.yml runs) is added and both its checks pass. Start the example server with
// --profile demo-source first. A rerun first removes the provider an earlier take made.
import { startRecording, signIn, BASE, OWNER } from '../lib.mjs';

const SOURCE_URL = 'http://example-source:8080';
const SOURCE_TOKEN = 'demo-only-example-token';
const NAME = 'Halcyon wiki';

const { accessToken } = await (await fetch(`${BASE}/api/v1/auth/token`, {
  method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(OWNER),
})).json();
const api = async (method, url) => {
  const res = await fetch(BASE + url, { method, headers: { Authorization: `Bearer ${accessToken}` } });
  if (!res.ok) throw new Error(`${method} ${url}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
};
const listed = await api('GET', '/api/v1/providers/custom');
// Connapse allows one provider per address, and the two-user setup's provider (same server) has connections on it.
for (const { provider, connections } of listed.filter(p => p.provider.baseUrl === SOURCE_URL)) {
  if (connections.length > 0) {
    throw new Error(`'${provider.name}' already uses ${SOURCE_URL} and has connections; record this shot on a fresh `
      + 'stack (reset.ps1, then setup-owner.mjs) or before setup-two-users.mjs.');
  }
  await api('DELETE', `/api/v1/providers/custom/${provider.id}`);
}

const state = await signIn(OWNER);
const r = await startRecording('connectors', { storageState: state });
const { page } = r;

await page.goto(`${BASE}/admin/providers`);
await page.getByText('Read files from S3.').waitFor({ timeout: 30_000 });
await page.waitForLoadState('networkidle');
r.mark('providers');
for (const text of ['Read files from S3.', 'Read files from Azure Blob Storage.',
  "Index repositories' docs, issues, and pull requests.", 'Index Confluence spaces and pages.']) {
  await r.moveTo(page.getByText(text));
  await r.pause(350);
}
await r.click(page.getByRole('button', { name: 'Add custom provider' }).or(page.getByRole('link', { name: 'Add custom provider' })).first());
const url = page.locator('#custom-provider-url');
await url.waitFor();
await page.waitForLoadState('networkidle');
r.mark('new-provider');
await r.type(url, SOURCE_URL, 30);
await r.type(page.locator('#custom-provider-token'), SOURCE_TOKEN, 20);
await r.click(page.locator('#custom-provider-check'));
await page.locator('#custom-provider-check-result').waitFor({ timeout: 30_000 });
r.mark('checked');
await r.moveTo(page.locator('#custom-provider-check-result'));
await r.pause(1500);
const name = page.locator('#custom-provider-name');
if (await name.inputValue() !== NAME) { await name.fill(''); await r.type(name, NAME, 30); }
await r.click(page.getByRole('button', { name: 'Save provider' }));
// Saving stays on the page and says what to do next.
const saved = page.getByText(`'${NAME}' saved.`);
await saved.waitFor({ timeout: 30_000 });
r.mark('saved');
await r.moveTo(saved);
await r.pause(2200);
r.mark('end');
await r.stop();
