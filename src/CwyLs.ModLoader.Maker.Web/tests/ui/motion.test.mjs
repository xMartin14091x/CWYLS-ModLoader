import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeT } from '../../ui/i18n/i18n.js';
import { renderSettingsPage } from '../../ui/zones/packs-modal.js';
import * as motionMod from '../../ui/motion.js';
import {
  MOTION_STORAGE_KEY,
  TAB_ORDER,
  clampPageMs,
  clampSmallMs,
  normalizeMotionSettings,
  loadMotionSettings,
  saveMotionSettings,
  applyMotionSettings,
  initMotion,
  setMotionSettings,
  directionOf,
  transitionPage,
  renderMotionSettings
} from '../../ui/motion.js';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');

function mockDoc() {
  const vars = new Map();
  const attrs = new Map();
  const root = {
    dataset: {},
    classList: { contains: () => false },
    setAttribute: (k, v) => attrs.set(k, String(v)),
    getAttribute: (k) => attrs.get(k) ?? null,
    style: {
      setProperty: (k, v) => vars.set(k, String(v)),
      getPropertyValue: (k) => vars.get(k) || ''
    }
  };
  return { documentElement: root, vars, attrs, getElementById: () => null };
}

function memStorage(initial = {}) {
  const map = new Map(Object.entries(initial));
  return {
    map,
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => map.set(k, String(v))
  };
}

test('clamps Page flow to 300-1000 ms (default 700) and Small UI to 150-400 ms (default 250)', () => {
  assert.equal(clampPageMs(undefined), 700);
  assert.equal(clampPageMs('not-a-number'), 700);
  assert.equal(clampPageMs(120), 300);
  assert.equal(clampPageMs(300), 300);
  assert.equal(clampPageMs(649.6), 650);
  assert.equal(clampPageMs(1000), 1000);
  assert.equal(clampPageMs(2400), 1000);

  assert.equal(clampSmallMs(undefined), 250);
  assert.equal(clampSmallMs(NaN), 250);
  assert.equal(clampSmallMs(40), 150);
  assert.equal(clampSmallMs(150), 150);
  assert.equal(clampSmallMs(275), 275);
  assert.equal(clampSmallMs(400), 400);
  assert.equal(clampSmallMs(950), 400);

  assert.deepEqual(normalizeMotionSettings({ mode: 'bogus', pageMs: 50, smallMs: 999 }), {
    mode: 'full',
    pageMs: 300,
    smallMs: 400
  });
});

test('prefers-reduced-motion starts in Reduced when nothing is saved, and saved choice wins', () => {
  const empty = memStorage();
  const matchReduce = (q) => ({ matches: q.includes('reduce') });
  const matchNone = () => ({ matches: false });

  assert.equal(loadMotionSettings(empty, matchNone).mode, 'full');
  assert.equal(loadMotionSettings(empty, matchReduce).mode, 'reduced');

  saveMotionSettings({ mode: 'full', pageMs: 820, smallMs: 310 }, empty);
  const loaded = loadMotionSettings(empty, matchReduce);
  assert.deepEqual(loaded, { mode: 'full', pageMs: 820, smallMs: 310 });

  const broken = {
    getItem() { throw new Error('SecurityError'); },
    setItem() { throw new Error('QuotaExceededError'); }
  };
  assert.doesNotThrow(() => loadMotionSettings(broken, matchReduce));
  assert.equal(loadMotionSettings(broken, matchReduce).mode, 'reduced');
  assert.doesNotThrow(() => saveMotionSettings({ mode: 'off', pageMs: 500, smallMs: 200 }, broken));
});

test('Motion Off / Reduced / Full sets --motion-page-ms and --motion-small-ms per MOTION-CONTRACT.md', () => {
  const doc = mockDoc();
  const store = memStorage();

  initMotion({ storage: store, doc, matchMediaFn: () => ({ matches: false }) });
  assert.equal(doc.documentElement.dataset.motionMode, 'full');
  assert.equal(parseFloat(doc.vars.get('--motion-page-ms')) || 0, 700);
  assert.equal(parseFloat(doc.vars.get('--motion-small-ms')) || 0, 250);

  setMotionSettings({ mode: 'off' }, { storage: store, doc });
  assert.equal(doc.documentElement.dataset.motionMode, 'off');
  assert.equal(parseFloat(doc.vars.get('--motion-page-ms')) || 0, 0);
  assert.equal(parseFloat(doc.vars.get('--motion-small-ms')) || 0, 0);
  assert.equal(JSON.parse(store.getItem(MOTION_STORAGE_KEY)).mode, 'off');

  setMotionSettings({ mode: 'reduced', pageMs: 700, smallMs: 250 }, { storage: store, doc });
  assert.equal(doc.documentElement.dataset.motionMode, 'reduced');
  const redPage = parseFloat(doc.vars.get('--motion-page-ms')) || 0;
  const redSmall = parseFloat(doc.vars.get('--motion-small-ms')) || 0;
  assert.ok(redPage > 0 && redPage <= 240, `reduced page duration ${redPage}`);
  assert.ok(redSmall > 0 && redSmall <= 200, `reduced small duration ${redSmall}`);

  applyMotionSettings({ mode: 'full', pageMs: 925, smallMs: 340 }, doc);
  assert.equal(parseFloat(doc.vars.get('--motion-page-ms')) || 0, 925);
  assert.equal(parseFloat(doc.vars.get('--motion-small-ms')) || 0, 340);
});

test('directionOf follows tab order and transitionPage swaps content via apply()', async () => {
  assert.deepEqual(TAB_ORDER, ['studio', 'lines', 'packs', 'export', 'settings']);
  assert.equal(directionOf('studio', 'lines'), 1);
  assert.equal(directionOf('lines', 'settings'), 1);
  assert.equal(directionOf('settings', 'packs'), -1);
  assert.equal(directionOf('export', 'studio'), -1);
  assert.equal(directionOf('packs', 'packs'), 0);

  let swapped = 0;
  const out = await transitionPage('studio', 'packs', async () => {
    swapped++;
    return 'packs-ready';
  });
  assert.equal(swapped, 1);
  assert.equal(out, 'packs-ready');
});

test('renderMotionSettings renders EN and casual TH controls and plugs into renderSettingsPage', () => {
  const s = {
    packId: 'demo.pack',
    packs: [{ id: 'demo.pack', name: 'Demo', editable: true, languages: ['th'] }],
    features: ['voiceDefault'],
    settings: { autoDoneOnNext: false, jumpAfterDone: true },
    motion: { mode: 'reduced', pageMs: 650, smallMs: 220 }
  };
  const enHtml = renderMotionSettings(s, makeT('en'));
  assert.match(enHtml, /data-act="motion-mode" data-id="off"/);
  assert.match(enHtml, /aria-checked="true" data-act="motion-mode" data-id="reduced"/);
  assert.match(enHtml, /data-act="motion-mode" data-id="full"/);
  assert.match(enHtml, /min="300" max="1000"[^]*value="650" data-in="motion-page"/);
  assert.match(enHtml, /min="150" max="400"[^]*value="220" data-in="motion-small"/);
  assert.match(enHtml, /Page flow/);
  assert.match(enHtml, /Small UI/);

  const thPage = renderSettingsPage(s, { T: makeT('th') });
  assert.match(thPage, /การเคลื่อนไหว/);
  assert.match(thPage, /จังหวะสลับหน้า/);
  assert.match(thPage, /เมนูและป๊อปอัป/);
  assert.ok(thPage.indexOf('data-ch="set-jump-done"') < thPage.indexOf('data-motion-settings="1"'),
    'renderMotionSettings sits at the bottom of .st-workflow');
});

test('motion.css owns [data-motion="pop"] states (is-opening / is-open / is-closing) and uses transform/opacity only', () => {
  const indexCss = readFileSync(resolve(ROOT, 'ui/styles/index.css'), 'utf8');
  const motionCss = readFileSync(resolve(ROOT, 'ui/styles/motion.css'), 'utf8');
  const motionJs = readFileSync(resolve(ROOT, 'ui/motion.js'), 'utf8');

  assert.match(indexCss, /@import url\(['"]\.\/motion\.css['"]\);/);
  assert.match(motionCss, /--motion-page-ms:\s*700ms/);
  assert.match(motionCss, /--motion-small-ms:\s*250ms/);
  assert.match(motionCss, /\[data-motion="pop"\]\.is-opening/);
  assert.match(motionCss, /\[data-motion="pop"\]\.is-open/);
  assert.match(motionCss, /\[data-motion="pop"\]\.is-closing/);
  assert.match(motionCss, /#top\s*\{\s*transform:\s*none\s*!important;/);

  // Guard CO Sidecar performance rule: no blur or SVG displacement filters on moving layers.
  assert.doesNotMatch(motionCss, /feDisplacementMap|feTurbulence|backdrop-filter:\s*blur|filter:\s*blur|filter:\s*url/i);
  assert.doesNotMatch(motionJs, /feDisplacementMap|feTurbulence|backdrop-filter:\s*blur|filter:\s*blur|filter:\s*url/i);

  // Guard outgoing stream visibility: .motion-out-layer z-index must sit strictly above incoming shell z-index
  // so stationary incoming .panel/.card chrome never buries outgoing Packs/Export/Settings clones.
  const outZMatch = motionCss.match(/\.motion-out-layer\s*\{[^}]*z-index:\s*(\d+)/);
  const inZMatch = motionCss.match(/#main\.is-streaming\s*>\s*\.page-view[^{]*\{[^}]*z-index:\s*(\d+)/s);
  assert.ok(outZMatch && inZMatch, 'motion.css defines explicit z-index for .motion-out-layer and #main.is-streaming > .page-view');
  assert.ok(Number(outZMatch[1]) > Number(inZMatch[1]), `.motion-out-layer z-index (${outZMatch[1]}) must exceed incoming shell z-index (${inZMatch[1]})`);
});

test('rebindIncomingStream preserves WAAPI animations and elapsed currentTime when #page-view re-renders mid-transition', async () => {
  assert.equal(typeof motionMod.rebindIncomingStream, 'function', 'rebindIncomingStream must be exported from ui/motion.js');
  const { rebindIncomingStream } = motionMod;

  const makeEl = (cls, top) => {
    const classes = new Set(cls.split(/\s+/).filter(Boolean));
    const anims = [];
    return {
      hidden: false,
      isConnected: true,
      classList: {
        add: (c) => classes.add(c),
        remove: (c) => classes.delete(c),
        contains: (c) => classes.has(c)
      },
      getBoundingClientRect: () => ({ left: 24, right: 1120, top, bottom: top + 68, width: 1096, height: 68 }),
      animate(keyframes, opts) {
        let resolveFin;
        const finished = new Promise((r) => { resolveFin = r; });
        const anim = {
          keyframes,
          opts,
          currentTime: 0,
          playState: 'running',
          effect: { target: this },
          finished,
          cancel() {
            this.playState = 'idle';
            resolveFin();
          }
        };
        anims.push(anim);
        return anim;
      },
      getAnimations() {
        return anims.filter((a) => a.playState === 'running');
      }
    };
  };

  let targets = [makeEl('pg-head', 64), makeEl('pcard', 148), makeEl('pcard', 230)];
  const mainClasses = new Set();
  const mainEl = {
    classList: {
      add: (c) => mainClasses.add(c),
      remove: (c) => mainClasses.delete(c),
      contains: (c) => mainClasses.has(c)
    },
    getBoundingClientRect: () => ({ left: 0, right: 1180, top: 48, bottom: 680, width: 1180, height: 632 }),
    querySelector: () => null,
    querySelectorAll: (sel) => (sel.includes('.pcard') ? targets : []),
    animate: () => ({ currentTime: 0, cancel() {}, finished: Promise.resolve() }),
    appendChild: () => {}
  };

  const prevDoc = globalThis.document;
  const mockDocument = mockDoc();
  mockDocument.getElementById = (id) => (id === 'main' ? mainEl : null);
  globalThis.document = mockDocument;
  try {
    setMotionSettings({ mode: 'full', pageMs: 700, smallMs: 250 }, { storage: memStorage(), doc: mockDocument });
    await transitionPage('studio', 'packs', () => {
      // Initial sync render before async loadPacksDetails resolves.
    });
    assert.equal(targets.every((t) => t.getAnimations().length === 1), true, 'initial Packs cards receive WAAPI animations');

    // Simulate paint() replacing #page-view.innerHTML 25ms later when loadPacksDetails resolves.
    const oldTargets = targets;
    for (const old of oldTargets) old.isConnected = false;
    await new Promise((r) => setTimeout(r, 25));
    targets = [makeEl('pg-head', 64), makeEl('pcard', 148), makeEl('pcard', 230), makeEl('pcard', 312)];

    rebindIncomingStream(mainEl);

    for (const fresh of targets) {
      const running = fresh.getAnimations();
      assert.equal(running.length, 1, 're-rendered Packs card must receive replacement WAAPI animation');
      assert.ok(running[0].currentTime >= 15, `replacement animation must advance to elapsed currentTime (got ${running[0].currentTime})`);
    }
    assert.equal(oldTargets.every((t) => t.getAnimations().length === 0), true, 'detached animations are cancelled cleanly');
  } finally {
    motionMod.cleanupActiveTransition?.();
    globalThis.document = prevDoc;
  }
});
