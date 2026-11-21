// List performance with the full invented catalogue (2,169 lines): load time, DOM size, scroll, filters, the Warnings chip.
// Budgets are deliberately plain and printed with the numbers: ready < 4 s, <= 60 row nodes, no task over 120 ms while scrolling/filtering.
// Run bounded:  perl -e 'alarm 120; exec @ARGV' node tests/e2e/perf.mjs
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, setViewport, loadStudio } from './cdp.mjs';
import { serveWeb } from './server.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
setTimeout(() => { console.error('WATCHDOG: perf took too long'); process.exit(2); }, 110000).unref();
const server = await serveWeb(ROOT), chrome = await launchChrome();
try {
  const page = await openPage(chrome.port);
  await setViewport(page, 1440, 900);
  const t0 = Date.now();
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=th`);
  const wall = Date.now() - t0;
  const lines = await page.eval('window.__studio.store.get().lines.length');
  assert.equal(lines, 2169);
  await page.eval(`window.__lt = []; new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__lt.push(Math.round(e.duration)); }).observe({ entryTypes: ['longtask'] }); true`);
  const result = await page.eval(`(async () => {
    const raf2 = () => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    const vl = document.querySelector('#vlist'), steps = [], nodes = [];
    const rh = parseFloat(getComputedStyle(document.documentElement).fontSize) * 2.75;
    for (let i = 0; i < 80; i++) {
      const t = performance.now();
      vl.scrollTop = i * rh * 25;
      vl.dispatchEvent(new Event('scroll'));
      await raf2();
      steps.push(performance.now() - t); nodes.push(document.querySelectorAll('.row, .vl-head').length);
    }
    const chip = async (id) => { const t = performance.now(); document.querySelector('[data-act="filter"][data-id="' + id + '"]').click(); await raf2(); return performance.now() - t; };
    const filters = {};
    for (const id of ['done', 'warn', 'warn', 'star', 'all']) filters[id + (filters[id] ? '2' : '')] = Math.round(await chip(id));
    const typed = []; const q = document.querySelector('[data-in=q]');
    for (const s of ['p', 'pr', 'pra', 'prac', 'pract']) { const t = performance.now(); q.value = s; q.dispatchEvent(new Event('input', { bubbles: true })); await raf2(); typed.push(Math.round(performance.now() - t)); }
    steps.sort((a, b) => a - b);
    return { steps: { median: Math.round(steps[40]), p95: Math.round(steps[76]), max: Math.round(steps[79]) }, maxNodes: Math.max(...nodes), filters, typed, longTasks: window.__lt.slice() };
  })()`, 60000);
  console.log(JSON.stringify({ lines, loadWallMs: wall, ...result }, null, 1));
  console.log('(each step includes two animation frames, about 33 ms at 60 Hz, so a step near 35 ms means the work itself is small)');
  assert.ok(wall < 4000, `ready took ${wall} ms`);
  assert.ok(result.maxNodes <= 60, `${result.maxNodes} row nodes in the DOM`);
  assert.ok(result.longTasks.every((d) => d < 120), `long tasks: ${result.longTasks}`);
  assert.deepEqual(page.problems, []);
  console.log('perf budgets ok');
} finally {
  await chrome.close();
  await server.close();
}
