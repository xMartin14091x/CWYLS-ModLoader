import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio, setViewport } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';
import { startFakeLoader } from '../e2e/fake-loader.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const pageChecksSrc = readFileSync(resolve(root, 'tests/e2e/page-checks.js'), 'utf8');
let server, fake, chrome, page;

before(async () => {
  server = await serveWeb(root);
  fake = await startFakeLoader({ tickSec: 60 });
  chrome = await launchChrome({
    extraArgs: ['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1']
  });
  page = await openPage(chrome.port);
});

after(async () => {
  page?.close();
  await chrome?.close();
  await fake?.close();
  await server?.close();
});

const q = (expr) => page.eval(expr);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const wait = async (expr, ms = 10000) => {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    if (await q(expr)) return;
    await sleep(30);
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

async function setupStudio(runTag, mode = 'full', pageMs = 760) {
  await setViewport(page, 1180, 700);
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=en&notour=1&r=${runTag}`);
  await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="settings"]').click()`,
    "__studio.store.get().page === 'settings' && !!document.querySelector('[data-act=\"motion-mode\"]')"
  );
  await q(`(() => {
    const sl = document.querySelector('input[data-in="motion-page"]');
    if (sl) {
      sl.value = String(${pageMs});
      sl.dispatchEvent(new Event('input', { bubbles: true }));
      sl.dispatchEvent(new Event('change', { bubbles: true }));
    }
    document.querySelector('[data-act="motion-mode"][data-id="${mode}"]').click();
  })()`);
  // Return to Studio cleanly and wait for any transition to finish.
  await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="studio"]').click()`,
    "__studio.store.get().page === 'studio' && !document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')"
  );
  await waitForFrames(2);
}

test('CO2 Lane C (Full): Packs, Export, and Settings flow IN and OUT with Scene current across async data loads', async () => {
  await setupStudio('co2-full', 'full', 1000);

  const topBefore = await q('Math.round(document.getElementById("top").getBoundingClientRect().top)');

  // 1. Studio -> Packs (navDir = +1, flowSign = -1: Studio flows out left, Packs flows in from right)
  const packsInSnap = await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="packs"]').click()`,
    `document.getElementById('main').classList.contains('is-streaming')
      && !!document.querySelector('.motion-out-layer')
      && /Creator \\(TH\\)/.test(document.querySelector('#page-view .pcard.cur')?.textContent || '')
      && (document.querySelector('#page-view .pcard')?.getAnimations().length || 0) > 0`,
    `(() => {
      const main = document.getElementById('main');
      const outHost = document.querySelector('.motion-out-layer');
      const pageView = document.getElementById('page-view');
      const cards = [...document.querySelectorAll('#page-view .pg-head, #page-view .packs-wins-note, #page-view .pcard')];
      const runningIn = cards.filter((el) => el.getAnimations().some((a) => a.playState === 'running'));
      const firstCard = document.querySelector('#page-view .pcard');
      const firstAnims = firstCard ? firstCard.getAnimations() : [];
      firstAnims.forEach((a) => {
        const t = a.effect?.getComputedTiming?.() || {};
        const delay = Number(t.delay || 0);
        const dur = Number(t.activeDuration || t.duration || 0);
        a.pause();
        a.currentTime = delay + dur * 0.15;
      });
      const firstCardLeftMid = firstCard ? firstCard.getBoundingClientRect().left : null;
      firstAnims.forEach((a) => a.play());
      const outItems = outHost?.shadowRoot ? [...outHost.shadowRoot.querySelectorAll('.out-item')] : [];
      const runningOut = outItems.filter((el) => el.getAnimations().some((a) => a.playState === 'running'));
      const outTextarea = outHost?.shadowRoot?.querySelector('textarea');
      const outTextareaBg = outTextarea ? getComputedStyle(outTextarea).backgroundColor : null;
      return {
        isStreaming: main.classList.contains('is-streaming'),
        hasAuthorLoaded: /Creator \\(TH\\)/.test(document.querySelector('#page-view .pcard.cur')?.textContent || ''),
        cardCount: cards.length,
        runningInCount: runningIn.length,
        firstCardLeftMid,
        outCount: outItems.length,
        runningOutCount: runningOut.length,
        outTextareaBg,
        outZ: outHost ? Number(getComputedStyle(outHost).zIndex) || 0 : 0,
        inZ: pageView ? Number(getComputedStyle(pageView).zIndex) || 0 : 0,
        topY: Math.round(document.getElementById('top').getBoundingClientRect().top)
      };
    })()`
  );

  assert.equal(packsInSnap.isStreaming, true, 'Packs transition must still be streaming after loadPacksDetails repaint');
  assert.equal(packsInSnap.hasAuthorLoaded, true, 'loadPacksDetails has already resolved and repainted #page-view');
  assert.ok(packsInSnap.runningInCount >= 4, `Packs incoming cards lost WAAPI animations after async loadPacksDetails (got ${packsInSnap.runningInCount}/${packsInSnap.cardCount})`);
  assert.ok(packsInSnap.runningOutCount >= 2, `Studio outgoing clones must run exit animations (got ${packsInSnap.runningOutCount})`);
  assert.equal(packsInSnap.outTextareaBg, 'rgba(0, 0, 0, 0)', 'Cloned outgoing textarea must remain transparent (no UA white flash)');
  assert.ok(packsInSnap.outZ > packsInSnap.inZ, `Outgoing .motion-out-layer (z=${packsInSnap.outZ}) must sit above incoming #page-view shell (z=${packsInSnap.inZ}) so outgoing stream is never occluded`);
  assert.equal(packsInSnap.topY, topBefore, '#top bar must never drift');

  await waitForState("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");
  const firstCardLeftRest = await q('document.querySelector("#page-view .pcard").getBoundingClientRect().left');
  assert.ok(
    packsInSnap.firstCardLeftMid > firstCardLeftRest + 30,
    `Packs .pcard must stream in from right (+X) toward rest (mid=${packsInSnap.firstCardLeftMid}, rest=${firstCardLeftRest})`
  );

  // 2. Packs -> Export (navDir = +1, flowSign = -1: Packs flows out left, Export flows in from right & survives runExportCheck)
  const packOutRestLeft = firstCardLeftRest;
  const exportSnap = await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="export"]').click()`,
    `document.getElementById('main').classList.contains('is-streaming')
      && !!document.querySelector('.motion-out-layer')
      && document.querySelectorAll('#page-view .chk-row').length === 7`,
    `(() => {
      const outHost = document.querySelector('.motion-out-layer');
      const pageView = document.getElementById('page-view');
      const sr = outHost?.shadowRoot;
      const outPcards = sr ? [...sr.querySelectorAll('.out-item')].filter((w) => w.querySelector('.pcard')) : [];
      const runningOutPcards = outPcards.filter((w) => w.getAnimations().some((a) => a.playState === 'running'));
      // Freeze each outgoing card at half of its own animation before measuring, so a busy CPU cannot make the
      // sample land before the drift starts (the old wall-clock sample failed under load: 23.47 vs 24.125).
      outPcards.forEach((w) => w.getAnimations().forEach((a) => { const d = a.effect?.getComputedTiming?.().endTime || 0; a.pause(); a.currentTime = d / 2; }));
      const outFirstLeft = outPcards[0]?.getBoundingClientRect().left ?? null;
      outPcards.forEach((w) => w.getAnimations().forEach((x) => x.play()));
      const inTargets = [...document.querySelectorAll('#page-view .pg-head, #page-view .exp-top, #page-view .exp-left, #page-view .exp-right, #page-view .exp-empty')];
      const runningIn = inTargets.filter((el) => el.getAnimations().some((a) => a.playState === 'running'));
      return {
        chkCount: document.querySelectorAll('#page-view .chk-row').length,
        outPcardCount: outPcards.length,
        runningOutPcards: runningOutPcards.length,
        outFirstLeft,
        inTargetCount: inTargets.length,
        runningInCount: runningIn.length,
        outZ: outHost ? Number(getComputedStyle(outHost).zIndex) || 0 : 0,
        inZ: pageView ? Number(getComputedStyle(pageView).zIndex) || 0 : 0
      };
    })()`
  );

  assert.equal(exportSnap.chkCount, 7, 'runExportCheck has resolved with all 7 checks');
  assert.ok(exportSnap.runningOutPcards >= 3, `Packs .pcard clones must flow out with running WAAPI animations (got ${exportSnap.runningOutPcards}/${exportSnap.outPcardCount})`);
  assert.ok(exportSnap.outFirstLeft !== null && exportSnap.outFirstLeft < packOutRestLeft - 15, `Packs outgoing .pcard must drift downstream left (got ${exportSnap.outFirstLeft} vs rest ${packOutRestLeft})`);
  assert.ok(exportSnap.outZ > exportSnap.inZ, `Packs outgoing layer (z=${exportSnap.outZ}) must not be occluded by Export #page-view (z=${exportSnap.inZ})`);
  assert.ok(exportSnap.runningInCount >= 3, `Export incoming cards must keep running WAAPI animations after runExportCheck repaint (got ${exportSnap.runningInCount}/${exportSnap.inTargetCount})`);

  await waitForState("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");

  // 3. Export -> Settings (survives loadVoiceDefault + scanUnconfirmedTakes; .st-workflow streams as a card)
  await q(`(() => {
    const raw = __studio.conn.api.raw;
    const origVd = raw.studioVoiceDefault.bind(raw);
    window.__settingsVoiceDone = false;
    window.__restoreVd = () => { raw.studioVoiceDefault = origVd; };
    raw.studioVoiceDefault = async (...args) => {
      const res = await origVd(...args);
      queueMicrotask(() => requestAnimationFrame(() => { window.__settingsVoiceDone = true; }));
      return res;
    };
  })()`);
  const settingsSnap = await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="settings"]').click()`,
    `window.__settingsVoiceDone === true
      && document.getElementById('main').classList.contains('is-streaming')
      && !!document.querySelector('#page-view .pg-head')
      && !!document.querySelector('#page-view .st-voice')
      && !!document.querySelector('#page-view .st-workflow')`,
    `(() => {
      const wf = document.querySelector('#page-view .st-workflow');
      const voice = document.querySelector('#page-view .st-voice');
      const head = document.querySelector('#page-view .pg-head');
      return {
        headRunning: head?.getAnimations().some((a) => a.playState === 'running') || false,
        voiceRunning: voice?.getAnimations().some((a) => a.playState === 'running') || false,
        wfRunning: wf?.getAnimations().some((a) => a.playState === 'running') || false
      };
    })()`
  );
  await q('window.__restoreVd?.()');

  assert.equal(settingsSnap.headRunning, true, 'Settings .pg-head must stream in');
  assert.equal(settingsSnap.voiceRunning, true, 'Settings .st-voice must keep streaming after loadVoiceDefault');
  assert.equal(settingsSnap.wfRunning, true, 'Settings .st-workflow card itself must stream in');

  await waitForState("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");

  // 4. Settings -> Packs -> Studio (reverse flowSign = +1: Packs flows out right un-occluded above Studio)
  await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="packs"]').click()`,
    "__studio.store.get().page === 'packs' && !document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer') && !!document.querySelector('#page-view .pcard')"
  );
  const packsRestBeforeStudio = await q('document.querySelector("#page-view .pcard").getBoundingClientRect().left');

  const packsToStudioSnap = await clickAndWaitForState(
    `document.querySelector('#top [data-act="page-tab"][data-id="studio"]').click()`,
    `document.getElementById('main').classList.contains('is-streaming')
      && ([...(document.querySelector('.motion-out-layer')?.shadowRoot?.querySelectorAll('.out-item') || [])].filter((w) => w.querySelector('.pcard')).length >= 3)`,
    `(() => {
      const outHost = document.querySelector('.motion-out-layer');
      const studioCard = document.getElementById('studio-card');
      const sr = outHost?.shadowRoot;
      const outPcards = sr ? [...sr.querySelectorAll('.out-item')].filter((w) => w.querySelector('.pcard')) : [];
      const runningOut = outPcards.filter((w) => w.getAnimations().some((a) => a.playState === 'running'));
      return {
        runningOut: runningOut.length,
        outFirstLeft: (() => { outPcards.forEach((w) => w.getAnimations().forEach((x) => { const d = x.effect?.getComputedTiming?.().endTime || 0; x.pause(); x.currentTime = d / 2; }));
          const left = outPcards[0]?.getBoundingClientRect().left ?? null; outPcards.forEach((w) => w.getAnimations().forEach((x) => x.play())); return left; })(),
        outZ: outHost ? Number(getComputedStyle(outHost).zIndex) || 0 : 0,
        studioZ: studioCard ? Number(getComputedStyle(studioCard).zIndex) || 0 : 0
      };
    })()`
  );

  assert.ok(packsToStudioSnap.runningOut >= 3, `Packs must flow out when switching to Studio (got ${packsToStudioSnap.runningOut})`);
  assert.ok(packsToStudioSnap.outFirstLeft !== null && packsToStudioSnap.outFirstLeft > packsRestBeforeStudio + 15, `Packs outgoing .pcard must flow rightward (+X) when returning to Studio (got ${packsToStudioSnap.outFirstLeft} vs ${packsRestBeforeStudio})`);
  assert.ok(packsToStudioSnap.outZ > packsToStudioSnap.studioZ, `Packs outgoing stream (z=${packsToStudioSnap.outZ}) must sit above #studio-card (z=${packsToStudioSnap.studioZ})`);

  await waitForState("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");
});

test('CO2 Lane C (Non-tab modal/page actions): Picker -> Packs, Pack card Export, and Pack card Open all flow in and out', async () => {
  await setupStudio('co2-actions', 'full', 720);

  // 1. Topbar Pack Picker -> "Packs" ([data-act="open-packs"]) flows into Packs
  await q(`document.querySelector('[data-act="pack-menu"]').click()`);
  await wait("!!document.querySelector('[data-act=\"open-packs\"]')");
  const openPacksRunning = await clickAndWaitForState(
    `document.querySelector('[data-act="open-packs"]').click()`,
    `document.getElementById('main').classList.contains('is-streaming') && document.querySelectorAll('#page-view .pcard').length > 0`,
    `[...document.querySelectorAll('#page-view .pcard')].every((el) => el.getAnimations().some((a) => a.playState === 'running'))`
  );
  assert.equal(openPacksRunning, true, 'Opening Packs from topbar Pack Picker must flow in');
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");

  // 2. Clicking "Export" button on a pack card ([data-act="open-export"]) flows out of Packs and into Export
  // Export checks arrive asynchronously. Observe their repaint inside the transition instead of sampling at 90 ms.
  const cardExportSnap = await clickAndWaitForState(
    `document.querySelector('#page-view .pcard [data-act="open-export"]').click()`,
    `document.getElementById('main').classList.contains('is-streaming') && document.querySelectorAll('#page-view .chk-row').length === 7`,
    `(() => {
    const outHost = document.querySelector('.motion-out-layer');
    const outRunning = outHost?.shadowRoot ? [...outHost.shadowRoot.querySelectorAll('.out-item')].filter((w) => w.getAnimations().some((a) => a.playState === 'running')).length : 0;
    const inRunning = [...document.querySelectorAll('#page-view .pg-head, #page-view .exp-top, #page-view .exp-left, #page-view .exp-right')].filter((el) => el.getAnimations().some((a) => a.playState === 'running')).length;
    return { page: __studio.store.get().page, outRunning, inRunning };
  })()`);
  assert.equal(cardExportSnap.page, 'export');
  assert.ok(cardExportSnap.outRunning >= 3, `Clicking Export on a pack card must flow Packs out (got ${cardExportSnap.outRunning})`);
  assert.ok(cardExportSnap.inRunning >= 3, `Clicking Export on a pack card must flow Export in (got ${cardExportSnap.inRunning})`);
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");

  // 3. Return to Packs, then click "Opened"/"Open" ([data-act="pick-pack"]) on a pack card -> flows Packs out & Studio in
  await q(`document.querySelector('#top [data-act="page-tab"][data-id="packs"]').click()`);
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");
  const pickPackSnap = await clickAndWaitForState(
    `document.querySelector('#page-view .pcard [data-act="pick-pack"]:not([disabled])').click()`,
    `__studio.store.get().page === 'studio' && document.getElementById('main').classList.contains('is-streaming') && !!document.querySelector('.motion-out-layer')`,
    `(() => {
    const outHost = document.querySelector('.motion-out-layer');
    const outRunning = outHost?.shadowRoot ? [...outHost.shadowRoot.querySelectorAll('.out-item')].filter((w) => w.getAnimations().some((a) => a.playState === 'running')).length : 0;
    const inRunning = [...document.querySelectorAll('#left .scn-nav, #left #vlist, #card, #studio-inspector')].filter((el) => el.getAnimations().some((a) => a.playState === 'running')).length;
    return { page: __studio.store.get().page, outRunning, inRunning };
  })()`);
  assert.equal(pickPackSnap.page, 'studio');
  assert.ok(pickPackSnap.outRunning >= 3, `Clicking Open on a pack card must flow Packs out (got ${pickPackSnap.outRunning})`);
  assert.ok(pickPackSnap.inRunning >= 3, `Clicking Open on a pack card must flow Studio in (got ${pickPackSnap.inRunning})`);
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");
});

test('CO2 Lane C (Reduced & Off): Packs, Export, and Settings fade in/out cleanly in Reduced and swap in 0ms when Off', async () => {
  // Reduced mode (~180-240ms calm opacity fade, surviving async loadModalData)
  await setupStudio('co2-reduced', 'reduced', 1000);

  await q(`document.querySelector('#top [data-act="page-tab"][data-id="packs"]').click()`);
  await sleep(55);

  const reducedPacks = await q(`(() => {
    const outHost = document.querySelector('.motion-out-layer');
    const outStage = outHost?.shadowRoot?.querySelector('.out-stage');
    const pcards = [...document.querySelectorAll('#page-view .pcard')];
    const runningIn = pcards.filter((el) => el.getAnimations().some((a) => a.playState === 'running'));
    const firstStyle = pcards[0] ? getComputedStyle(pcards[0]) : null;
    return {
      runningIn: runningIn.length,
      outRunning: outStage?.getAnimations().some((a) => a.playState === 'running') || false,
      opacity: firstStyle ? Number(firstStyle.opacity) : null,
      transform: firstStyle ? firstStyle.transform : null
    };
  })()`);

  assert.ok(reducedPacks.runningIn >= 3, `Reduced mode Packs cards must keep running fade-in across loadPacksDetails (got ${reducedPacks.runningIn})`);
  assert.equal(reducedPacks.outRunning, true, 'Reduced mode outgoing stage must fade out');
  assert.ok(reducedPacks.opacity !== null && reducedPacks.opacity < 0.99, `Reduced mode mid-fade opacity must be < 1 (got ${reducedPacks.opacity})`);
  assert.equal(reducedPacks.transform, 'none', 'Reduced mode must not translate incoming cards');

  await wait("!document.querySelector('.motion-out-layer')");

  // Off mode (0ms instant swap, zero animations)
  await setupStudio('co2-off', 'off', 700);
  for (const tab of ['packs', 'export', 'settings', 'studio']) {
    await q(`document.querySelector('#top [data-act="page-tab"][data-id="${tab}"]').click()`);
    const offSnap = await q(`(() => ({
      page: __studio.store.get().page,
      hasOut: !!document.querySelector('.motion-out-layer'),
      streaming: document.getElementById('main').classList.contains('is-streaming'),
      pageMs: getComputedStyle(document.documentElement).getPropertyValue('--motion-page-ms').trim()
    }))()`);
    assert.equal(offSnap.page, tab);
    assert.equal(offSnap.hasOut, false, `Motion Off must never mount .motion-out-layer on ${tab}`);
    assert.equal(offSnap.streaming, false, `Motion Off must never set .is-streaming on ${tab}`);
    assert.equal(offSnap.pageMs, '0ms');
  }
});

test('CO2 Lane C (Real HTTP adapter + New Pack + layout checks): Packs/Export/Settings flow in & out over real HTTP with zero overflow at 1180x700 and 1440x900', async () => {
  await setViewport(page, 1180, 700);
  await loadStudio(page, `${fake.url}index.html?backend=http&lang=th&notour=1&r=co2-http`);
  await q(pageChecksSrc);
  await q(`document.querySelector('#top [data-act="page-tab"][data-id="settings"]').click()`);
  await wait("!!document.querySelector('[data-act=\"motion-mode\"]')");
  await q(`(() => {
    const sl = document.querySelector('input[data-in="motion-page"]');
    if (sl) {
      sl.value = '760';
      sl.dispatchEvent(new Event('input', { bubbles: true }));
      sl.dispatchEvent(new Event('change', { bubbles: true }));
    }
    document.querySelector('[data-act="motion-mode"][data-id="full"]').click();
    document.querySelector('#top [data-act="page-tab"][data-id="studio"]').click();
  })()`);
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer') && !document.getElementById('studio-card').hidden");
  await sleep(60);

  // Real HTTP: Studio -> Packs
  await q(`document.querySelector('#top [data-act="page-tab"][data-id="packs"]').click()`);
  await sleep(160);
  const httpPacksIn = await q(`(() => ({
    runningIn: [...document.querySelectorAll('#page-view .pg-head, #page-view .packs-wins-note, #page-view .pcard')].filter((el) => el.getAnimations().some((a) => a.playState === 'running')).length,
    runningOut: [...(document.querySelector('.motion-out-layer')?.shadowRoot?.querySelectorAll('.out-item') || [])].filter((el) => el.getAnimations().some((a) => a.playState === 'running')).length
  }))()`);
  assert.ok(httpPacksIn.runningIn >= 3, `HTTP Packs incoming cards must keep streaming after HTTP packInfo/groups (got ${httpPacksIn.runningIn})`);
  assert.ok(httpPacksIn.runningOut >= 2, `HTTP Studio outgoing clones must keep streaming (got ${httpPacksIn.runningOut})`);
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");

  // Packs -> Voice lines -> Packs (New pack form via picker)
  await q(`document.querySelector('#top [data-act="page-tab"][data-id="lines"]').click()`);
  await sleep(160);
  const packsToLinesOut = await q(`(() => {
    const sr = document.querySelector('.motion-out-layer')?.shadowRoot;
    return sr ? [...sr.querySelectorAll('.out-item')].filter((w) => w.querySelector('.pcard') && w.getAnimations().some((a) => a.playState === 'running')).length : 0;
  })()`);
  assert.ok(packsToLinesOut >= 2, `HTTP Packs -> Voice lines must flow Packs cards out (got ${packsToLinesOut})`);
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");

  await q(`document.querySelector('[data-act="pack-menu"]').click()`);
  await wait("!!document.querySelector('[data-act=\"new-pack\"]')");
  await q(`document.querySelector('#top [data-act="new-pack"]').click()`);
  await sleep(160);
  const newPackIn = await q(`(() => ({
    headRunning: document.querySelector('#page-view .pg-head')?.getAnimations().some((a) => a.playState === 'running') || false,
    cardRunning: document.querySelector('#page-view .m-newpack')?.getAnimations().some((a) => a.playState === 'running') || false
  }))()`);
  assert.equal(newPackIn.headRunning, true, 'New pack .pg-head must stream in');
  assert.equal(newPackIn.cardRunning, true, 'New pack .m-newpack card must stream in as a unit');
  await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");

  // Verify layout & Thai ink checks at rest on Packs, Export, and Settings across 1180x700 and 1440x900
  for (const [w, h] of [[1180, 700], [1440, 900]]) {
    await setViewport(page, w, h);
    for (const tab of ['packs', 'export', 'settings']) {
      await q(`document.querySelector('#top [data-act="page-tab"][data-id="${tab}"]').click()`);
      await wait("!document.getElementById('main').classList.contains('is-streaming') && !document.querySelector('.motion-out-layer')");
      await sleep(40);
      const chk = await q('window.__pmChecks.run()');
      assert.equal(chk.noPageScroll, true, `${tab} at ${w}x${h} caused page scroll: scrollW=${chk.scrollW}, scrollH=${chk.scrollH}`);
      assert.equal(chk.clipped.length, 0, `${tab} at ${w}x${h} clipped text: ${JSON.stringify(chk.clipped)}`);
    }
  }
});
