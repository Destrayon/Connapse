// Shot 3: Dana and Sam ask the payments wiki source the same question. The example server's grants decide
// what each sees: Dana holds only the shared folder's key, Sam also holds finance's. Run setup-two-users.mjs first.
import { startRecording, signIn, BASE, USERS } from '../lib.mjs';

const QUESTION = 'who approves a large refund?';
const sourceId = process.argv[2];
if (!sourceId) throw new Error('Usage: node two-users.mjs <payments source id>');

for (const who of ['dana', 'sam']) {
  const u = USERS[who];
  const r = await startRecording(`two-users-${who}`, { storageState: await signIn(u) });
  const { page } = r;
  await page.goto(`${BASE}/search?scope=${sourceId}`);
  const box = page.locator('input[placeholder="Enter your search query..."]');
  await box.waitFor();
  await page.waitForLoadState('networkidle');
  r.mark('ready');
  await r.moveTo(page.getByText(u.name, { exact: true }));
  await r.pause(900);
  await r.type(box, QUESTION, 35);
  await r.click(page.getByRole('button', { name: 'Search', exact: true }));
  await page.getByText('Search Results').waitFor();
  r.mark('results');
  await r.moveTo(page.locator('.results-header'));
  await r.pause(2500);
  r.mark('end');
  await r.stop();
}
