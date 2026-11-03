// Setup for the source and provider shots (not recorded): on an empty stack, create the owner and the halcyon
// container that quickstart.mjs would otherwise create on camera. Skip it after the quickstart shot.
import { chromium } from 'playwright';
import { BASE, OWNER } from '../lib.mjs';

const browser = await chromium.launch();
const page = await browser.newPage();
await page.goto(`${BASE}/register`);
await page.waitForLoadState('networkidle');
await page.getByRole('textbox', { name: 'Email' }).fill(OWNER.email);
await page.getByRole('textbox', { name: 'Display Name' }).fill('Halcyon Admin');
await page.getByRole('textbox', { name: 'Password', exact: true }).fill(OWNER.password);
await page.getByRole('textbox', { name: 'Confirm Password' }).fill(OWNER.password);
await page.getByRole('button', { name: 'Create Owner Account' }).click();
await page.waitForURL(u => !u.pathname.startsWith('/register'));
await browser.close();

const { accessToken } = await (await fetch(`${BASE}/api/v1/auth/token`, {
  method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(OWNER),
})).json();
const res = await fetch(`${BASE}/api/containers`, {
  method: 'POST',
  headers: { Authorization: `Bearer ${accessToken}`, 'content-type': 'application/json' },
  body: JSON.stringify({ name: 'halcyon', description: 'Halcyon engineering docs' }),
});
if (!res.ok) throw new Error(`POST /api/containers: ${res.status} ${await res.text()}`);
console.log(await res.json());
