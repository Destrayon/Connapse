// Shared Playwright helpers for the demo recordings: video capture, a visible cursor, and time marks.
import { chromium } from 'playwright';
import { mkdirSync, renameSync, writeFileSync } from 'node:fs';
import path from 'node:path';

export const BASE = process.env.DEMO_URL ?? 'http://localhost:5201';
export const RAW = process.env.DEMO_RAW ?? 'E:/tmp/con-200/raw';
export const OWNER = { email: 'owner@halcyon.example', password: 'Demo-only-Passw0rd!' };
export const SIZE = { width: 1280, height: 720 };
/** Shot 3's users; each key is also the example server user they are linked to. */
export const USERS = {
  dana: { email: 'dana@halcyon.example', name: 'Dana (support)', password: 'Demo-only-Dana-1!' },
  sam: { email: 'sam@halcyon.example', name: 'Sam (finance)', password: 'Demo-only-Sam-1!' },
};

const CURSOR = `
  window.addEventListener('DOMContentLoaded', () => {
    const c = document.createElement('div');
    c.id = '__demo_cursor';
    c.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;width:18px;height:18px;' +
      'border-radius:50%;background:rgba(255,255,255,.85);border:2px solid #8b5cf6;' +
      'transform:translate(-50%,-50%);left:-40px;top:-40px;transition:width .1s,height .1s';
    document.body.appendChild(c);
    document.addEventListener('mousemove', e => { c.style.left = e.clientX + 'px'; c.style.top = e.clientY + 'px'; }, true);
    document.addEventListener('mousedown', () => { c.style.width = c.style.height = '12px'; }, true);
    document.addEventListener('mouseup', () => { c.style.width = c.style.height = '18px'; }, true);
  });`;

/** Starts a recorded browser. `marks` collects named seconds since the video began. */
export async function startRecording(name, { storageState } = {}) {
  mkdirSync(RAW, { recursive: true });
  const browser = await chromium.launch();
  const context = await browser.newContext({
    viewport: SIZE, colorScheme: 'dark', storageState,
    recordVideo: { dir: RAW, size: SIZE },
  });
  await context.addInitScript(CURSOR);
  const page = await context.newPage();
  const t0 = Date.now();
  const marks = {};
  let mouse = { x: SIZE.width / 2, y: SIZE.height / 2 };

  const rec = {
    page, context, marks,
    mark(label) { marks[label] = (Date.now() - t0) / 1000; },
    async moveTo(locator) {
      await locator.waitFor({ state: 'visible' });
      let b = await locator.boundingBox();
      // Blazor can re-render the element between the visibility check and the measurement.
      for (let i = 0; !b && i < 20; i++) { await page.waitForTimeout(100); b = await locator.boundingBox(); }
      if (!b) throw new Error(`No position for ${locator}`);
      const to = { x: b.x + b.width / 2, y: b.y + b.height / 2 };
      await page.mouse.move(mouse.x, mouse.y);
      await page.mouse.move(to.x, to.y, { steps: 18 });
      mouse = to;
    },
    async click(locator) {
      await rec.moveTo(locator);
      await page.waitForTimeout(120);
      await locator.click();
    },
    async type(locator, text, delay = 45) {
      await rec.click(locator);
      await locator.pressSequentially(text, { delay });
    },
    pause(ms) { return page.waitForTimeout(ms); },
    /** Closes the browser and saves the video as RAW/<name>.webm with its marks beside it. */
    async stop() {
      const video = page.video();
      await context.close();
      await browser.close();
      const out = path.join(RAW, `${name}.webm`);
      renameSync(await video.path(), out);
      writeFileSync(path.join(RAW, `${name}.marks.json`), JSON.stringify(marks, null, 2));
      console.log(out, marks);
      return out;
    },
  };
  return rec;
}

/** Signs in through the login form in an unrecorded browser and returns the session for startRecording. */
export async function signIn({ email, password }) {
  const browser = await chromium.launch();
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(`${BASE}/login`);
  await page.getByRole('textbox', { name: 'Email' }).fill(email);
  await page.getByRole('textbox', { name: 'Password' }).fill(password);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await page.waitForURL(u => !u.pathname.startsWith('/login'));
  const state = await context.storageState();
  await browser.close();
  return state;
}
