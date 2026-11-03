// Provider settings: the embedding provider's fields follow the provider picked, Test Connection reaches the running
// Ollama, and the LLM tab offers its own providers. Nothing is saved, so a rerun needs no cleanup.
import { startRecording, signIn, BASE, OWNER } from '../lib.mjs';

const state = await signIn(OWNER);
const r = await startRecording('provider-settings', { storageState: state });
const { page } = r;

// Each tab fills its form once its settings load; a pick made before then is overwritten.
// networkidle is not enough here; 1.5 s held in every try.
const settle = async () => { await page.waitForLoadState('networkidle'); await r.pause(1500); };
// Pick with the arrow keys: after selectOption the video never repaints the select or the fields it swaps in.
const pick = async (select, value) => {
  const at = () => select.evaluate((s, v) => [s.selectedIndex, [...s.options].findIndex(o => o.value === v)], value);
  const [from, to] = await at();
  await select.focus();
  for (let i = from; i !== to; i += Math.sign(to - from)) {
    await page.keyboard.press(to > from ? 'ArrowDown' : 'ArrowUp');
    await r.pause(250);
  }
  await r.pause(400);
  if (await select.inputValue() !== value) throw new Error(`The ${value} pick did not hold`);
};
// Jump rather than scroll: every scrolled frame is new pixels and bloats the GIF.
const jump = y => page.evaluate(top => window.scrollTo({ top: top ?? document.body.scrollHeight, behavior: 'instant' }), y);

await page.goto(`${BASE}/settings`);
const provider = page.locator('select').first();
await provider.waitFor();
await settle();
r.mark('embedding');
await r.moveTo(provider);
await pick(provider, 'OpenAI');
r.mark('openai');
await r.pause(1600);
await pick(provider, 'Ollama');
r.mark('ollama');
await r.pause(900);

await jump(null);
await r.pause(300);
await r.click(page.getByRole('button', { name: 'Test Connection' }));
const ok = page.locator('.alert-success', { hasText: 'Connected to Ollama' });
await ok.waitFor({ timeout: 30_000 });
await jump(null);
r.mark('tested');
await r.moveTo(ok);
await r.pause(1800);

await jump(0);
await r.click(page.getByRole('button', { name: 'LLM', exact: true }));
const llm = page.locator('select').first();
await page.getByTestId('llm-unused-notice').waitFor();
await settle();
r.mark('llm');
await r.moveTo(llm);
await pick(llm, 'Anthropic');
await page.locator('#anthropicBaseUrl').waitFor({ timeout: 10_000 });
r.mark('anthropic');
await r.pause(1800);
r.mark('end');
await r.stop();
