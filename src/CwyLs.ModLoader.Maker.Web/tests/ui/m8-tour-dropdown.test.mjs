// Regression test for M8: Voice lines step-1 guide bubble must not block clicks on open dropdown options.
import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio, setViewport } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
let server, chrome, page;

before(async () => {
  server = await serveWeb(root);
  chrome = await launchChrome({
    extraArgs: ['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1']
  });
  page = await openPage(chrome.port);
});

after(async () => {
  page?.close();
  await chrome?.close();
  await server?.close();
});

const q = (expr) => page.eval(expr);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const wait = async (expr, ms = 5000) => {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    if (await q(expr)) return;
    await sleep(40);
  }
  throw new Error(`Timed out: ${expr}`);
};

async function clickCdp(x, y) {
  await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
  await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
}

test('M8: Voice lines step-1 tour bubble hides and does not intercept clicks on open speaker dropdown options', async () => {
  for (const [w, h] of [[1180, 700], [1440, 900]]) {
    await setViewport(page, w, h);
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=en&notour=1`);
    await q(`(() => {
      localStorage.removeItem('cwyls-maker-tours-seen');
      __studio.store.set({ motion: { mode: 'off', pageMs: 700, smallMs: 250 } });
      document.documentElement.dataset.motionMode = 'off';
      document.documentElement.style.setProperty('--motion-small-ms', '0ms');
      __studio.store.set({ page: 'lines', modal: null, tour: { page: 'lines', step: 0 } });
    })()`);
    await wait("!document.querySelector('#tour-layer').hidden && !!document.querySelector('#tour-card')");

    const btnPt = await q(`(() => {
      const r = document.querySelector('[data-dd-btn="dd-charfilter"]').getBoundingClientRect();
      return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    })()`);
    await clickCdp(btnPt.x, btnPt.y);
    await wait("document.querySelector('[data-dd=\"dd-charfilter\"]').classList.contains('open')");
    await wait("getComputedStyle(document.getElementById('tour-layer')).opacity === '0'");

    const blocked = await q(`(() => {
      const list = document.querySelector('[data-dd="dd-charfilter"] .c-dd-list');
      const out = [];
      for (const opt of list.querySelectorAll('.c-dd-opt')) {
        const r = opt.getBoundingClientRect();
        for (const [pos, x, y] of [
          ['left', r.left + 12, r.top + r.height / 2],
          ['mid', r.left + r.width / 2, r.top + r.height / 2],
          ['right', r.right - 12, r.top + r.height / 2]
        ]) {
          const hit = document.elementFromPoint(x, y);
          if (!list.contains(hit)) out.push({ opt: opt.dataset.ddOpt, pos, hit: hit?.tagName + '#' + hit?.id + '.' + hit?.className });
        }
      }
      return out;
    })()`);
    assert.deepEqual(blocked, [], `Viewport ${w}x${h}: hidden tour bubble blocked dropdown options: ${JSON.stringify(blocked)}`);

    // Real pointer click on the right edge of "Satone" (where the hidden tour bubble overlaps) must select satone.
    const pt = await q(`(() => {
      const r = document.querySelector('[data-dd="dd-charfilter"] [data-dd-opt="satone"]').getBoundingClientRect();
      return { x: r.right - 12, y: r.top + r.height / 2 };
    })()`);
    await clickCdp(pt.x, pt.y);
    await wait("!document.querySelector('[data-dd=\"dd-charfilter\"]').classList.contains('open')");
    assert.equal(await q('__studio.store.get().view.char'), 'satone', `Viewport ${w}x${h}: clicking Satone must filter speaker`);
  }
});
