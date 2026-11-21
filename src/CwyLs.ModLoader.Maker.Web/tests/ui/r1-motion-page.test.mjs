import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio, setViewport } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const pageChecksSrc = readFileSync(resolve(root, 'tests/e2e/page-checks.js'), 'utf8');
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
const wait = async (expr, ms = 10000) => {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    if (await q(expr)) return;
    await sleep(40);
  }
  throw new Error(`Timed out: ${expr}`);
};
const waitForState = (predExpr, snapExpr = 'true', timeoutMs = 10000) =>
  page.eval(
    `new Promise((resolve, reject) => {
      const check = () => {
        try {
          if (${predExpr}) return { ok: true, value: (${snapExpr}) };
        } catch (err) {
          return { ok: false, err };
        }
        return null;
      };
      const first = check();
      if (first) {
        if (first.ok) { resolve(first.value); return; }
        reject(first.err);
        return;
      }
      let done = false;
      let rafId = 0;
      const finish = (ok, val) => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        if (rafId) cancelAnimationFrame(rafId);
        obs.disconnect();
        if (ok) resolve(val); else reject(val);
      };
      const tick = () => {
        if (done) return;
        const res = check();
        if (res) { finish(res.ok, res.ok ? res.value : res.err); return; }
        rafId = requestAnimationFrame(tick);
      };
      const obs = new MutationObserver(() => {
        const res = check();
        if (res) finish(res.ok, res.ok ? res.value : res.err);
      });
      obs.observe(document.documentElement, { childList: true, subtree: true, attributes: true });
      rafId = requestAnimationFrame(tick);
      const timer = setTimeout(() => finish(false, new Error(${JSON.stringify(`Timed out: ${predExpr}`)})), ${timeoutMs});
    })`,
    timeoutMs + 2000
  );
const clickAndWaitForState = (clickExpr, predExpr, snapExpr = 'true', timeoutMs = 10000) =>
  page.eval(
    `new Promise((resolve, reject) => {
      const check = () => {
        try {
          if (${predExpr}) return { ok: true, value: (${snapExpr}) };
        } catch (err) {
          return { ok: false, err };
        }
        return null;
      };
      let done = false;
      let rafId = 0;
      const finish = (ok, val) => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        if (rafId) cancelAnimationFrame(rafId);
        obs.disconnect();
        if (ok) resolve(val); else reject(val);
      };
      const tick = () => {
        if (done) return;
        const res = check();
        if (res) { finish(res.ok, res.ok ? res.value : res.err); return; }
        rafId = requestAnimationFrame(tick);
      };
      const obs = new MutationObserver(() => {
        const res = check();
        if (res) finish(res.ok, res.ok ? res.value : res.err);
      });
      obs.observe(document.documentElement, { childList: true, subtree: true, attributes: true });
      const timer = setTimeout(() => finish(false, new Error(${JSON.stringify(`Timed out: ${predExpr}`)})), ${timeoutMs});
      (${clickExpr});
      const immediate = check();
      if (immediate) { finish(immediate.ok, immediate.ok ? immediate.value : immediate.err); return; }
      rafId = requestAnimationFrame(tick);
    })`,
    timeoutMs + 2000
  );
const waitForFrames = (count = 2) =>
  page.eval(`new Promise((resolve) => {
    let left = ${count};
    const step = () => (--left <= 0 ? resolve(true) : requestAnimationFrame(step));
    requestAnimationFrame(step);
  })`);

async function pressEnterCdp() {
  await page.send('Input.dispatchKeyEvent', {
    type: 'keyDown',
    key: 'Enter',
    code: 'Enter',
    windowsVirtualKeyCode: 13,
    nativeVirtualKeyCode: 13,
    text: '\r',
    unmodifiedText: '\r'
  });
  await page.send('Input.dispatchKeyEvent', {
    type: 'keyUp',
    key: 'Enter',
    code: 'Enter',
    windowsVirtualKeyCode: 13,
    nativeVirtualKeyCode: 13
  });
}

test('HMCM P2-4: pressing Enter on companion Next steps through without calling completeLine and restores focus to "?" on close', async () => {
  await setViewport(page, 1180, 700);
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=en&r=p24`);
  await q(`(() => {
    localStorage.removeItem('cwyls-maker-tours-seen');
    __studio.actions.selectLine('morning_wake_006');
  })()`);
  await wait("__studio.store.get().lineId === 'morning_wake_006'");
  const initialStatus = await q('__studio.store.get().line.status');

  // Open companion via "?" -> Start the guide (or reset) with instant small motion for deterministic stepping.
  await q(`(() => {
    __studio.store.set({ motion: { mode: 'off', pageMs: 700, smallMs: 250 } });
    document.documentElement.dataset.motionMode = 'off';
    document.documentElement.style.setProperty('--motion-small-ms', '0ms');
    document.querySelector('[data-act="keys-menu"]').click();
  })()`);
  await wait("!!document.querySelector('[data-act=\"tour-start\"]')");
  await q("document.querySelector('[data-act=\"tour-start\"]').click()");
  await wait("!document.querySelector('#tour-layer').hidden && !!document.querySelector('[data-act=\"tour-next\"]')");

  assert.equal(await q('document.activeElement?.dataset?.act'), 'tour-next', 'Bubble Next button focused on open');

  // Press real CDP Enter across all five Studio steps, including voice origin.
  for (const expectedStep of [1, 2, 3, 4]) {
    await pressEnterCdp();
    await wait(`__studio.store.get().tour?.step === ${expectedStep}`);
    assert.equal(await q('document.activeElement?.dataset?.act'), 'tour-next', `Next stays focused at step ${expectedStep}`);
    assert.equal(await q('__studio.store.get().lineId'), 'morning_wake_006', 'Enter on Next must not jump lines');
    assert.equal(await q('__studio.store.get().line.status'), initialStatus, 'Enter on Next must not mark line Done');
  }

  // Final Enter on "Done" closes the companion and focuses "?" ([data-act="keys-menu"]).
  await pressEnterCdp();
  await wait("document.querySelector('#tour-layer').hidden");
  assert.equal(await q('__studio.store.get().lineId'), 'morning_wake_006');
  assert.equal(await q('__studio.store.get().line.status'), initialStatus);
  assert.equal(await q('document.activeElement?.dataset?.act'), 'keys-menu', 'Focus restored to "?" on close');
});

test('HMCM P2-5: clicking Export tab starts page transition immediately before slow exportCheck resolves, with no late clone flash', async () => {
  await setViewport(page, 1180, 700);
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=en&notour=1&r=p25`);
  await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="settings"]').click()`,
    "__studio.store.get().page === 'settings' && !!document.querySelector('[data-act=\"motion-mode\"]')"
  );
  await q(`(() => {
    const sl = document.querySelector('input[data-in="motion-page"]');
    if (sl) {
      sl.value = '400';
      sl.dispatchEvent(new Event('input', { bubbles: true }));
      sl.dispatchEvent(new Event('change', { bubbles: true }));
    }
    document.querySelector('[data-act="motion-mode"][data-id="full"]').click();
  })()`);
  await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="studio"]').click()`,
    "__studio.store.get().page === 'studio' && !document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')"
  );
  await waitForFrames(2);

  await q(`(() => {
    const raw = __studio.conn.api.raw;
    const origCheck = raw.studioExportCheck.bind(raw);
    window.__exportCheckDone = false;
    window.__exportGate = new Promise((r) => { window.__releaseExport = r; });
    raw.studioExportCheck = async (...args) => {
      await window.__exportGate;
      const res = await origCheck(...args);
      window.__exportCheckDone = true;
      return res;
    };
  })()`);

  // Click Export tab while studioExportCheck is gated on the network and capture immediately in-page.
  const startSnap = await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="export"]').click()`,
    "__studio.store.get().page === 'export' && document.getElementById('main').classList.contains('is-streaming')",
    `({
      hasOutLayer: !!document.querySelector('.motion-out-layer'),
      hasExpEmpty: !!document.querySelector('#page-view .m-export .exp-empty'),
      hasChkList: !!document.querySelector('#page-view .m-export .chk-list'),
      exportCheckDone: window.__exportCheckDone === true
    })`
  );
  assert.equal(startSnap.hasOutLayer, true, 'Outgoing Studio clone streams immediately at click time');
  assert.equal(startSnap.hasExpEmpty, true, 'Export loading spinner renders immediately before exportCheck resolves');
  assert.equal(startSnap.hasChkList, false, 'Empty 0-row .chk-list must not render before exportCheck resolves');
  assert.equal(startSnap.exportCheckDone, false, 'exportCheck must still be gated when transition starts');

  // Wait for the transition to finish while exportCheck is STILL waiting on the network.
  const doneBeforeRelease = await waitForState(
    "!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')",
    "window.__exportCheckDone === true"
  );
  assert.equal(doneBeforeRelease, false, 'Transition must finish before gated exportCheck resolves');

  // Arm observer on #main so any late clone or streaming class after release is caught, then release exportCheck.
  await q(`(() => {
    window.__lateCloneSeen = false;
    const main = document.getElementById('main');
    window.__lateObs = new MutationObserver(() => {
      if (document.querySelector('.motion-out-layer') || main.classList.contains('is-streaming')) {
        window.__lateCloneSeen = true;
      }
    });
    window.__lateObs.observe(main, { childList: true, attributes: true, attributeFilter: ['class'] });
    window.__releaseExport();
  })()`);

  await waitForState(
    "window.__exportCheckDone === true && !__studio.store.get().exportLoading && document.querySelectorAll('#page-view .m-export .chk-row').length === 7"
  );
  await waitForFrames(2);
  const endSnap = await q(`(() => {
    window.__lateObs?.disconnect();
    return {
      hasOutLayer: !!document.querySelector('.motion-out-layer'),
      lateCloneSeen: window.__lateCloneSeen === true
    };
  })()`);
  assert.equal(endSnap.hasOutLayer, false, 'Network completion must not mount a late clone of Studio');
  assert.equal(endSnap.lateCloneSeen, false, 'Network completion must not flash a late clone or re-enter streaming');
});

test('HMC-2: leaving scrolled Voice lines (scrollTop=4000) renders visible rows in the outgoing Scene F clone', async () => {
  await setViewport(page, 1180, 700);
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=en&notour=1&r=hmc2`);
  await q(`(() => {
    const s = __studio.store.get(), base = s.lines[0];
    const lines = Array.from({ length: 2168 }, (_, i) => ({ ...base, id: 'synthetic-' + i, status: 'todo' }));
    document.documentElement.dataset.motionMode = 'full';
    document.documentElement.style.setProperty('--motion-page-ms', '700ms');
    __studio.store.set({ lines, page: 'lines', modal: null, motion: { mode: 'full', pageMs: 700, smallMs: 250 } });
  })()`);
  await wait("document.querySelectorAll('.vb-row').length > 0");
  await q(`(() => {
    const list = document.getElementById('vb-list');
    list.scrollTop = 4000;
    list.dispatchEvent(new Event('scroll'));
  })()`);
  await wait("(() => { const r = document.querySelector('.vb-row'); return r && r.dataset.id !== 'synthetic-0'; })()");

  // Trigger transition from scrolled Voice lines -> Studio and inspect ShadowRoot clone mid-stream.
  await q("document.querySelector('#top [data-act=\"page-tab\"][data-id=\"studio\"]').click()");
  await wait("!!document.querySelector('.motion-out-layer')");

  const cloneInfo = await q(`(() => {
    const host = document.querySelector('.motion-out-layer');
    const sr = host?.shadowRoot || host;
    const tbody = sr?.querySelector('.vb-tbody');
    const spacers = tbody ? tbody.querySelectorAll('[aria-hidden="true"]').length : -1;
    const rows = tbody ? [...tbody.querySelectorAll('.vb-row')] : [];
    const firstRect = rows[0]?.getBoundingClientRect();
    const tableRect = sr?.querySelector('.vb-table')?.getBoundingClientRect();
    return {
      spacers,
      rowCount: rows.length,
      firstRowTop: firstRect ? Math.round(firstRect.top) : null,
      tableBottom: tableRect ? Math.round(tableRect.bottom) : null
    };
  })()`);

  assert.equal(cloneInfo.spacers, 0, 'Cloned .vb-tbody must not include virtual scroll spacers');
  assert.ok(cloneInfo.rowCount >= 5, `Expected visible cloned rows, got ${cloneInfo.rowCount}`);
  assert.ok(
    cloneInfo.firstRowTop !== null && cloneInfo.firstRowTop < cloneInfo.tableBottom && cloneInfo.firstRowTop < 700,
    `First cloned row must sit inside the visible viewport, got top=${cloneInfo.firstRowTop}`
  );
});

test('HMC-3: "?" Help Popover at 1180x700 stays <= 1180px (noPageScroll=true) during cwyls-pop-in', async () => {
  for (const lang of ['th', 'en']) {
    await setViewport(page, 1180, 700);
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=${lang}&notour=1&r=hmc3-${lang}`);
    await q(pageChecksSrc);
    await q(`(() => {
      document.documentElement.dataset.motionMode = 'full';
      document.documentElement.style.setProperty('--motion-small-ms', '280ms');
      __studio.store.set({ motion: { mode: 'full', pageMs: 700, smallMs: 280 } });
    })()`);

    await q("document.querySelector('[data-act=\"keys-menu\"]').click()");
    await wait("!!document.querySelector('.help-menu')");

    await sleep(16);
    const check16 = await q('window.__pmChecks.run()');
    await sleep(45);
    const check60 = await q('window.__pmChecks.run()');
    assert.equal(check16.noPageScroll, true, `${lang} 16ms pop-in overflowed: scrollW=${check16.scrollW} ${JSON.stringify(check16.overflowing)}`);
    assert.equal(check60.noPageScroll, true, `${lang} 60ms pop-in overflowed: scrollW=${check60.scrollW} ${JSON.stringify(check60.overflowing)}`);
  }
});

test('HMC-4 & HMC-1 (DOM): "?" Help Popover keeps keycaps single-line, right-aligns descriptions, and renders guide link', async () => {
  for (const lang of ['th', 'en']) {
    await setViewport(page, 1180, 700);
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=${lang}&notour=1&r=hmc4-${lang}`);
    await q(`(() => {
      document.documentElement.dataset.motionMode = 'off';
      document.documentElement.style.setProperty('--motion-small-ms', '0ms');
      __studio.store.set({ motion: { mode: 'off', pageMs: 700, smallMs: 250 } });
    })()`);

    await q("document.querySelector('[data-act=\"keys-menu\"]').click()");
    await wait("!!document.querySelector('.help-menu')");

    const layout = await q(`(() => {
      const rows = [...document.querySelectorAll('.help-menu .keys-list .k-row')];
      const kbdCells = rows.map((r) => {
        const first = r.firstElementChild;
        const last = r.lastElementChild;
        return {
          text: first.textContent.trim(),
          h: first.clientHeight,
          ws: getComputedStyle(first).whiteSpace,
          descAlign: getComputedStyle(last).textAlign
        };
      });
      const link = document.querySelector('.help-menu a.help-guide-link');
      return { kbdCells, href: link?.getAttribute('href') || null };
    })()`);

    for (const c of layout.kbdCells) {
      assert.equal(c.ws, 'nowrap', `${lang} keycap "${c.text}" must be nowrap`);
      assert.ok(c.h <= 22, `${lang} keycap "${c.text}" wrapped onto multiple lines (clientHeight=${c.h})`);
      assert.equal(c.descAlign, 'right', `${lang} shortcut description for "${c.text}" must align right`);
    }
    const expectedDoc = lang === 'th' ? 'pack-maker.th.md' : 'pack-maker.md';
    assert.ok(layout.href && layout.href.endsWith(`/docs/guide/${expectedDoc}`), `${lang} guide href=${layout.href}`);
  }
});
