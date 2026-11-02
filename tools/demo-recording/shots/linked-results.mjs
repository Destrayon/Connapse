// Shot 5: the same question over the example server's code, with the link layer off and then on. Links come from
// the code itself (calls, imports, constructor types), so with them on the files AccessPolicy depends on rise and
// the test files that only share its words fall. The UI does not mark linked hits, so each take gets a caption and a
// panel listing the ranked file names read from that page's own result cards. Run setup-agent.mjs first.
import { startRecording, signIn, BASE, OWNER } from '../lib.mjs';

const QUESTION = 'how does the server decide which keys a user holds';

const { accessToken } = await (await fetch(`${BASE}/api/v1/auth/token`, {
  method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(OWNER),
})).json();
const headers = { Authorization: `Bearer ${accessToken}`, 'content-type': 'application/json' };
const api = async (method, url, body) => {
  const res = await fetch(BASE + url, { method, headers, body: body && JSON.stringify(body) });
  if (!res.ok) throw new Error(`${method} ${url}: ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
};
async function setLinkSearch(on) {
  const settings = await api('GET', '/api/settings/search');
  await api('PUT', '/api/settings/search', { ...settings, enableLinkSearch: on });
}

const code = (await api('GET', '/api/containers')).items.find(c => c.name === 'example-server');
const state = await signIn(OWNER);
let before = [];

try {
  for (const on of [false, true]) {
    await setLinkSearch(on);
    const r = await startRecording(`linked-results-${on ? 'on' : 'off'}`, { storageState: state });
    const { page } = r;
    await page.goto(`${BASE}/search?scope=${code.id}`);
    const box = page.locator('input[placeholder="Enter your search query..."]');
    await box.waitFor();
    await page.waitForLoadState('networkidle');
    await page.evaluate(([text, on]) => {
      const chip = document.createElement('div');
      chip.textContent = text;
      chip.style.cssText = 'position:fixed;top:14px;right:18px;z-index:2147483646;padding:6px 14px;border-radius:999px;' +
        `font:600 15px system-ui,sans-serif;color:#fff;background:${on ? '#16a34a' : '#475569'}`;
      document.body.appendChild(chip);
    }, [on ? 'Link search: on' : 'Link search: off', on]);
    r.mark('ready');
    await r.type(box, QUESTION, 30);
    await r.click(page.getByRole('button', { name: 'Search', exact: true }));
    await page.getByText('Search Results').waitFor();
    r.mark('results');
    const files = await page.locator('.result-card .source-badge').allTextContents();
    await page.evaluate(([files, before]) => {
      const panel = document.createElement('div');
      panel.style.cssText = 'position:fixed;top:60px;right:18px;z-index:2147483646;width:330px;padding:12px 16px;' +
        'border-radius:10px;background:#0f172a;border:1px solid #334155;font:15px/1.65 system-ui,sans-serif;color:#e2e8f0';
      // A file's first appearance in each list; a rise of three or more places is called out.
      const isNew = f => before.length && !before.includes(f);
      const rose = (f, i) => before.length && files.indexOf(f) === i && before.indexOf(f) - i >= 3;
      const note = (f, i) => isNew(f) ? 'new, via a link' : rose(f, i) ? `up from #${before.indexOf(f) + 1}` : '';
      panel.innerHTML = '<div style="color:#94a3b8;font-size:13px;margin-bottom:4px">Top results, by file</div>' +
        files.map((f, i) => `<div><span style="color:#64748b;display:inline-block;width:28px">#${i + 1}</span>` +
          `<span style="color:${note(f, i) ? '#4ade80' : /Tests\.cs$/.test(f) ? '#94a3b8' : '#e2e8f0'}">${f}</span>` +
          (note(f, i) ? ` <span style="color:#4ade80;font-size:12px">${note(f, i)}</span>` : '') + '</div>').join('');
      document.body.appendChild(panel);
    }, [files, before]);
    await page.getByText('Top results, by file').waitFor();
    before = files;
    await r.pause(on ? 3800 : 2800);
    r.mark('end');
    await r.stop();
  }
} finally {
  await setLinkSearch(false);
}
