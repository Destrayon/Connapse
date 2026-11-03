// Shot 2, browser half: on a fresh install, create the owner, make a container, upload a PDF,
// search it and land on the result citing that file. Run right after the quickstart terminal scene.
import { fileURLToPath } from 'node:url';
import { startRecording, BASE, OWNER } from '../lib.mjs';

const pdf = fileURLToPath(new URL('../corpus/halcyon-operations-handbook.pdf', import.meta.url));
const r = await startRecording('quickstart-browser');
const { page } = r;

await page.goto(`${BASE}/register`);
await page.getByRole('button', { name: 'Create Owner Account' }).waitFor();
r.mark('register');
await r.type(page.getByRole('textbox', { name: 'Email' }), OWNER.email, 25);
await r.type(page.getByRole('textbox', { name: 'Display Name' }), 'Halcyon Admin', 25);
await r.type(page.getByRole('textbox', { name: 'Password', exact: true }), OWNER.password, 15);
await r.type(page.getByRole('textbox', { name: 'Confirm Password' }), OWNER.password, 15);
await r.click(page.getByRole('button', { name: 'Create Owner Account' }));
await page.waitForURL(u => !u.pathname.startsWith('/register'));
await page.getByRole('button', { name: /New Container/ }).waitFor();
r.mark('home');

await r.click(page.getByRole('button', { name: /New Container/ }));
await r.type(page.getByRole('textbox', { name: 'my-project' }), 'halcyon', 40);
await r.click(page.getByRole('button', { name: 'Create', exact: true }));
await r.click(page.getByRole('heading', { name: 'halcyon' }));
await page.waitForURL(/\/containers\//);
r.mark('container');

const chooser = page.waitForEvent('filechooser');
await r.click(page.locator('label[for="fileUploadInput"]'));
await (await chooser).setFiles(pdf);
r.mark('uploaded');
await page.locator('.badge', { hasText: 'Ready' }).waitFor({ timeout: 180_000 });
r.mark('ready');
await r.pause(1500);

await r.click(page.getByRole('link', { name: 'Search' }));
const box = page.locator('input[placeholder="Enter your search query..."]');
await box.waitFor();
await page.waitForLoadState('networkidle');
r.mark('search');
await r.type(box, 'what do I do when the card processor times out?', 30);
await r.click(page.getByRole('button', { name: 'Search', exact: true }));
await page.locator('.result-card').first().waitFor();
r.mark('results');
await r.moveTo(page.locator('.source-badge').first());
await r.pause(2200);
r.mark('end');
await r.stop();
