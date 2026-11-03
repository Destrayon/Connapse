// Renders corpus/halcyon-operations-handbook.html to a PDF next to it.
import { chromium } from 'playwright';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), 'corpus');
const browser = await chromium.launch();
const page = await browser.newPage();
await page.goto(pathToFileURL(path.join(dir, 'halcyon-operations-handbook.html')).href);
await page.pdf({ path: path.join(dir, 'halcyon-operations-handbook.pdf'), format: 'Letter' });
await browser.close();
