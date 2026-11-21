import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeT } from '../../ui/i18n/i18n.js';
import {
  TOUR_STORAGE_KEY,
  loadSeenTours,
  isPageTourSeen,
  markPageTourSeen,
  resetSeenTours,
  TOUR_STEPS,
  tourStepsFor,
  clampTourStep,
  nextTourState,
  computeCompanionLayout,
  buildCursorGlideKeyframes,
  renderHelpPopover,
  renderTourOverlay,
  syncTourCompanion,
  teardownTourCompanion
} from '../../ui/zones/tour.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = path.resolve(__dirname, '../..');

function fakeStorage(initial = {}) {
  const map = new Map(Object.entries(initial));
  return {
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => map.set(k, String(v)),
    removeItem: (k) => map.delete(k)
  };
}

function rectOverlap(ax1, ay1, ax2, ay2, bx1, by1, bx2, by2) {
  const ox = Math.max(0, Math.min(ax2, bx2) - Math.max(ax1, bx1));
  const oy = Math.max(0, Math.min(ay2, by2) - Math.max(ay1, by1));
  return ox * oy;
}

test('seen tours per page are stored in localStorage (5B rule) and reset cleanly; corrupt or throwing storage never throws', () => {
  const st = fakeStorage();
  for (const page of ['studio', 'lines', 'packs', 'export', 'settings']) {
    assert.equal(isPageTourSeen(page, st), false, `${page} unseen initially`);
  }
  markPageTourSeen('studio', st);
  assert.equal(isPageTourSeen('studio', st), true);
  assert.equal(isPageTourSeen('lines', st), false);

  markPageTourSeen('lines', st);
  assert.deepEqual(loadSeenTours(st), { studio: true, lines: true });

  resetSeenTours(st);
  assert.equal(st.getItem(TOUR_STORAGE_KEY), null);
  assert.equal(isPageTourSeen('studio', st), false);

  const corrupt = fakeStorage({ [TOUR_STORAGE_KEY]: '{not-json' });
  assert.deepEqual(loadSeenTours(corrupt), {});
  assert.equal(isPageTourSeen('studio', corrupt), false);

  const broken = {
    getItem: () => { throw new Error('quota'); },
    setItem: () => { throw new Error('quota'); },
    removeItem: () => { throw new Error('quota'); }
  };
  assert.doesNotThrow(() => {
    assert.equal(isPageTourSeen('studio', broken), false);
    markPageTourSeen('studio', broken);
    resetSeenTours(broken);
  });
});

test('renderHelpPopover keeps "How to use this page", "How the other pages connect", shortcuts, and "Start the guide" in EN & TH', () => {
  const expectedBtn = { en: 'Start the guide', th: 'เริ่มตัวช่วยแนะนำ' };
  for (const lang of ['en', 'th']) {
    const T = makeT(lang);
    for (const page of ['studio', 'lines', 'packs', 'export', 'settings']) {
      const html = renderHelpPopover({ page }, T);
      assert.match(html, /class="menu keys-menu help-menu"/);
      assert.match(html, /data-motion="pop"/);
      assert.match(html, /class="help-steps"/);
      assert.match(html, /class="help-connect"/);
      assert.match(html, /class="keys-list"/);
      assert.match(html, /data-act="tour-start"/);
      assert.ok(html.includes(expectedBtn[lang]), `${lang} ${page} shows "${expectedBtn[lang]}"`);
    }
  }
});

test('every page has >= 3 companion steps (including settings) with EN + casual TH what-it-is and what-to-do copy', () => {
  const pages = ['studio', 'lines', 'packs', 'export', 'settings'];
  assert.equal(TOUR_STEPS.studio.length, 5, 'Studio covers scene, subtitle, capture, origin and playback');
  for (const page of pages) {
    const steps = tourStepsFor(page);
    assert.ok(steps.length >= 3, `${page} must have at least 3 steps (got ${steps.length})`);
    for (const lang of ['en', 'th']) {
      const T = makeT(lang);
      steps.forEach((st, i) => {
        assert.ok(st.selector, `${page}[${i}] has selector`);
        const title = T(st.titleKey);
        const what = T(st.whatKey);
        const body = T(st.bodyKey);
        assert.notEqual(title, st.titleKey, `${lang} ${st.titleKey} translated`);
        assert.notEqual(what, st.whatKey, `${lang} ${st.whatKey} translated`);
        assert.notEqual(body, st.bodyKey, `${lang} ${st.bodyKey} translated`);
      });
    }
  }
});

test('step reducer (clampTourStep & nextTourState) handles Next, Back, Done, and Skip across every page', () => {
  for (const page of ['studio', 'lines', 'packs', 'export', 'settings']) {
    const total = tourStepsFor(page).length;
    assert.equal(clampTourStep(page, -5).index, 0);
    assert.equal(clampTourStep(page, 99).index, total - 1);

    let state = { page, step: 0 };
    assert.deepEqual(nextTourState(state, 'prev'), { page, step: 0 }, 'Back at step 0 stays at 0');

    for (let i = 1; i < total; i++) {
      state = nextTourState(state, 'next');
      assert.deepEqual(state, { page, step: i });
    }
    const backOne = nextTourState(state, 'prev');
    assert.deepEqual(backOne, { page, step: total - 2 });
    const atEnd = nextTourState(backOne, 'next');
    assert.deepEqual(atEnd, { page, step: total - 1 });
    assert.equal(nextTourState(atEnd, 'next'), null, 'Next on final step finishes tour');
    assert.equal(nextTourState({ page, step: 1 }, 'skip'), null, 'Skip closes tour');
  }
});

test('renderTourOverlay renders spotlight, guiding cursor and plain step card', () => {
  for (const lang of ['en', 'th']) {
    const T = makeT(lang);
    for (const page of ['studio', 'lines', 'packs', 'export', 'settings']) {
      const steps = tourStepsFor(page);
      const html0 = renderTourOverlay({ page, tour: { page, step: 0 } }, T);
      assert.ok(!/tour-ring/.test(html0), 'must not render old spotlight ring');
      assert.match(html0, /id="tour-pos"/);
      assert.match(html0, /id="tour-spotlight"/);
      assert.match(html0, /id="tour-shade" fill-rule="evenodd"/);
      assert.match(html0, /id="tour-card"[^>]*data-motion="pop"/);
      assert.match(html0, /id="tour-cursor"/);
      assert.match(html0, /class="comp-cursor-svg"/);
      assert.match(html0, /class="tour-what"/, 'renders what-it-is explanation');
      assert.match(html0, /class="comp-do"/, 'renders what-to-do callout');
      assert.match(html0, /data-act="tour-prev" disabled/);
      assert.match(html0, /data-act="tour-next"/);
      assert.match(html0, /data-act="tour-skip"/);

      const htmlLast = renderTourOverlay({ page, tour: { page, step: steps.length - 1 } }, T);
      assert.ok(!/data-act="tour-prev" disabled/.test(htmlLast), 'Back enabled on last step');
      assert.ok(htmlLast.includes(T('tourDone')), 'Last step shows Done label');
    }
  }
});

test('computeCompanionLayout never clips off-screen at 1180x700 or 1440x900 and never covers the guiding cursor', () => {
  const viewports = [
    { width: 1180, height: 700 },
    { width: 1440, height: 900 }
  ];
  const bubbleSizes = [
    { width: 328, height: 178 },
    { width: 340, height: 206 }
  ];
  const targets = [
    { name: 'top-left scene nav', rect: { left: 14, top: 58, width: 256, height: 42 }, preferSide: 'right', anchor: { rx: 0.78, ry: 0.54 } },
    { name: 'top-right corner button', rect: { left: 1130, top: 54, width: 38, height: 34 }, preferSide: 'left', anchor: { rx: 0.5, ry: 0.5 } },
    { name: 'bottom-left control', rect: { left: 16, top: 642, width: 210, height: 42 }, preferSide: 'above', anchor: { rx: 0.4, ry: 0.5 } },
    { name: 'bottom-right hear-in-game', rect: { left: 900, top: 636, width: 258, height: 44 }, preferSide: 'above', anchor: { rx: 0.52, ry: 0.45 } },
    { name: 'wide subtitle editor', rect: { left: 296, top: 184, width: 856, height: 78 }, preferSide: 'below', anchor: { rx: 0.24, ry: 0.52 } },
    { name: 'tall check list', rect: { left: 28, top: 170, width: 540, height: 470 }, preferSide: 'right', anchor: { rx: 0.46, ry: 0.24 } },
    { name: 'oversized scrolled container', rect: { left: 20, top: -80, width: 700, height: 920 }, preferSide: 'below', anchor: { rx: 0.3, ry: 0.5 } },
    { name: 'missing target fallback', rect: null, preferSide: 'auto', anchor: null }
  ];

  for (const vp of viewports) {
    for (const bs of bubbleSizes) {
      for (const t of targets) {
        const res = computeCompanionLayout({
          targetRect: t.rect,
          bubbleSize: bs,
          viewport: vp,
          preferSide: t.preferSide,
          anchor: t.anchor
        });
        const label = `${vp.width}x${vp.height} (${bs.width}x${bs.height}) [${t.name}]`;
        assert.ok(res.bx >= 16, `${label}: bx=${res.bx} >= 16`);
        assert.ok(res.by >= 52, `${label}: by=${res.by} >= 52`);
        assert.ok(res.bx + res.bw <= vp.width - 16, `${label}: right=${res.bx + res.bw} <= ${vp.width - 16}`);
        assert.ok(res.by + res.bh <= vp.height - 16, `${label}: bottom=${res.by + res.bh} <= ${vp.height - 16}`);

        assert.ok(res.cx >= 0 && res.cx + 36 <= vp.width, `${label}: cursor X within viewport`);
        assert.ok(res.cy >= 0 && res.cy + 40 <= vp.height, `${label}: cursor Y within viewport`);

        if (t.rect) {
          // Verify bubble does not sit on top of the cursor pointer tip region.
          const overlap = rectOverlap(
            res.bx,
            res.by,
            res.bx + res.bw,
            res.by + res.bh,
            res.cx + 2,
            res.cy + 2,
            res.cx + 28,
            res.cy + 30
          );
          assert.equal(overlap, 0, `${label}: bubble must not cover guiding cursor (overlap=${overlap})`);
        }
      }
    }
  }
});

test('buildCursorGlideKeyframes produces a smooth Scene-current curve using transform ONLY', () => {
  const frames = buildCursorGlideKeyframes({ x: 180, y: 90 }, { x: 620, y: 390 });
  assert.ok(frames.length >= 3);
  for (const kf of frames) {
    const keys = Object.keys(kf).filter((k) => k !== 'offset' && k !== 'easing');
    assert.deepEqual(keys, ['transform'], 'keyframes must animate transform ONLY');
    assert.match(kf.transform, /^translate3d\(/);
  }
  // Midpoint deviates from straight chord to create an organic curved glide, then lands at exact destination 0deg.
  const midMatch = frames[1].transform.match(/translate3d\(([-\d.]+)px,\s*([-\d.]+)px/);
  assert.ok(midMatch);
  const mx = Number(midMatch[1]);
  const my = Number(midMatch[2]);
  const chordYAtMx = 90 + ((mx - 180) / (620 - 180)) * (390 - 90);
  assert.ok(Math.abs(my - chordYAtMx) > 4, 'cursor path follows a gentle Scene-current arc');
  assert.equal(frames[frames.length - 1].transform, 'translate3d(620px, 390px, 0) rotate(0deg)');
});

test('spotlight keeps static SVG geometry and companion motion uses only transform and opacity', () => {
  const css = fs.readFileSync(path.join(WEB_ROOT, 'ui/styles/overlays.css'), 'utf8');
  const js = fs.readFileSync(path.join(WEB_ROOT, 'ui/zones/tour.js'), 'utf8');

  assert.ok(!/\.tour-ring\b/.test(css), 'overlays.css must not define .tour-ring');
  assert.ok(!/9999px/.test(css), 'overlays.css must not use a 9999px screen-dimming box-shadow');

  const compCssSlice = css.slice(css.indexOf('.tour-layer'));
  assert.ok(compCssSlice.length > 100, 'companion CSS section exists');
  assert.ok(
    !/transition\s*:[^;]*(?:\btop\b|\bleft\b|\bwidth\b|\bheight\b)/i.test(compCssSlice),
    'companion CSS must never transition top/left/width/height'
  );
  assert.ok(
    !/backdrop-filter\s*:\s*blur|filter\s*:\s*blur|filter\s*:\s*url/i.test(compCssSlice),
    'companion CSS must not put blur/SVG filters on moving elements'
  );
  assert.ok(
    !/\.style\.(left|top|width|height)\s*=/.test(js),
    'tour.js must position companion elements via translate3d, never .style.left/.top/.width/.height'
  );
});

test('syncTourCompanion keeps persistent cursor/bubble DOM across steps, respects Full / Reduced / Off motion, and tracks resize without jumps', () => {
  const animLog = [];
  const listeners = new Map();

  function parseTranslate3d(str) {
    const m = String(str || '').match(/translate3d\(([-\d.]+)px,\s*([-\d.]+)px/);
    return m ? { x: Number(m[1]), y: Number(m[2]) } : { x: 0, y: 0 };
  }

  function makeEl(id, rect = { left: 0, top: 0, width: 328, height: 176 }) {
    const attrs = new Map();
    const classes = new Set();
    const el = {
      id,
      hidden: false,
      dataset: {},
      offsetWidth: rect.width,
      offsetHeight: rect.height,
      style: {
        transform: '',
        setProperty(k, v) { this[k] = v; },
        getPropertyValue(k) { return this[k] || ''; }
      },
      classList: {
        add: (...cs) => cs.forEach((c) => classes.add(c)),
        remove: (...cs) => cs.forEach((c) => classes.delete(c)),
        contains: (c) => classes.has(c)
      },
      setAttribute: (k, v) => attrs.set(k, String(v)),
      getAttribute: (k) => attrs.get(k) ?? null,
      getClientRects: () => [rect],
      getBoundingClientRect: () => {
        if (id === 'tour-pos' || id === 'tour-cursor') {
          const p = parseTranslate3d(el.style.transform);
          return { left: p.x, top: p.y, width: rect.width, height: rect.height };
        }
        return { ...rect };
      },
      animate: (keyframes, options) => {
        const rec = { id, keyframes, options, canceled: false, cancel() { this.canceled = true; } };
        animLog.push(rec);
        return rec;
      }
    };
    return el;
  }

  let vw = 1440;
  let vh = 900;
  const targetsBySelector = {
    '.scn-nav': makeEl('scn-nav', { left: 16, top: 62, width: 252, height: 42 }),
    '#ed': makeEl('editor', { left: 310, top: 190, width: 880, height: 76 }),
    '#deck-body .d-mic, #deck-body .d-nomic': makeEl('d-mic', { left: 310, top: 410, width: 180, height: 108 })
  };

  const root = makeEl('html');
  root.setAttribute('data-motion-mode', 'full');
  root.style.setProperty('--motion-small-ms', '250ms');
  root.style.setProperty('--motion-page-ms', '700ms');

  let nodes = new Map();
  let contentHtml = '';
  const layer = makeEl('tour-layer');
  layer.hidden = true;
  Object.defineProperty(layer, 'innerHTML', {
    get: () => contentHtml,
    set: (val) => {
      contentHtml = val;
      nodes = new Map();
      if (val) {
        nodes.set('tour-pos', makeEl('tour-pos', { left: 0, top: 0, width: 328, height: 176 }));
        nodes.set('tour-card', makeEl('tour-card', { left: 0, top: 0, width: 328, height: 176 }));
        nodes.set('tour-tail', makeEl('tour-tail', { left: 0, top: 0, width: 12, height: 12 }));
        nodes.set('tour-cursor', makeEl('tour-cursor', { left: 0, top: 0, width: 36, height: 40 }));
        nodes.set('tour-ripple', makeEl('tour-ripple', { left: 0, top: 0, width: 42, height: 42 }));
        nodes.set('tour-content', {
          id: 'tour-content',
          get outerHTML() { return contentHtml; },
          set outerHTML(next) { contentHtml = next; },
          animate: (keyframes, options) => {
            const rec = { id: 'tour-content', keyframes, options, cancel() {} };
            animLog.push(rec);
            return rec;
          }
        });
      }
    }
  });

  let roCallback = null;
  class FakeResizeObserver {
    constructor(cb) { roCallback = cb; }
    observe() {}
    unobserve() {}
    disconnect() {}
  }

  const fakeWin = {
    get innerWidth() { return vw; },
    get innerHeight() { return vh; },
    scrollX: 0,
    scrollY: 0,
    ResizeObserver: FakeResizeObserver,
    addEventListener: (ev, fn) => listeners.set(ev, fn),
    removeEventListener: (ev) => listeners.delete(ev)
  };

  const fakeDoc = {
    documentElement: root,
    defaultView: fakeWin,
    addEventListener: () => {},
    removeEventListener: () => {},
    getElementById: (id) => (id === 'tour-layer' ? layer : nodes.get(id) || null),
    querySelectorAll: (sel) => (targetsBySelector[sel] ? [targetsBySelector[sel]] : [])
  };

  const T = makeT('en');
  syncTourCompanion({ page: 'studio', tour: { page: 'studio', step: 0 }, motion: { mode: 'full', pageMs: 700, smallMs: 250 } }, T, { lang: 'en', doc: fakeDoc });
  assert.equal(layer.hidden, false);
  const posRef = nodes.get('tour-pos');
  const curRef = nodes.get('tour-cursor');
  assert.ok(posRef && curRef);

  // Step 0 -> Step 1 in Full motion: keeps exact same DOM nodes and glides via WAAPI transform keyframes.
  animLog.length = 0;
  syncTourCompanion({ page: 'studio', tour: { page: 'studio', step: 1 }, motion: { mode: 'full', pageMs: 700, smallMs: 250 } }, T, { lang: 'en', doc: fakeDoc });
  assert.equal(nodes.get('tour-pos'), posRef, 'tour-pos preserved across steps without DOM rebuild');
  assert.equal(nodes.get('tour-cursor'), curRef, 'tour-cursor preserved across steps without DOM rebuild');
  assert.ok(animLog.some((a) => a.id === 'tour-cursor' && a.keyframes.length >= 3), 'Full mode glides cursor along curved arc');
  assert.ok(animLog.some((a) => a.id === 'tour-pos'), 'Full mode glides bubble positioner');

  // Initial ResizeObserver notification on the newly observed step target must NOT cancel in-flight glide when target did not move.
  const curGlide = animLog.find((a) => a.id === 'tour-cursor');
  assert.equal(typeof roCallback, 'function', 'the companion registered a ResizeObserver callback (a missing one must fail, not skip)');
  roCallback();
  assert.equal(curGlide.canceled, false, 'Unmoved ResizeObserver tick keeps in-flight cursor glide alive');

  // Reduced motion: step change uses calm opacity fade, zero spatial cursor/bubble arc.
  root.setAttribute('data-motion-mode', 'reduced');
  root.style.setProperty('--motion-small-ms', '180ms');
  animLog.length = 0;
  syncTourCompanion({ page: 'studio', tour: { page: 'studio', step: 2 }, motion: { mode: 'reduced', pageMs: 700, smallMs: 250 } }, T, { lang: 'en', doc: fakeDoc });
  assert.equal(animLog.some((a) => a.id === 'tour-pos'), false, 'Reduced mode skips spatial bubble travel');
  assert.ok(animLog.every((a) => a.keyframes.every((kf) => !('transform' in kf))), 'Reduced mode animates opacity only');

  // Off motion: 0 WAAPI animations on step change.
  root.setAttribute('data-motion-mode', 'off');
  root.style.setProperty('--motion-small-ms', '0ms');
  root.style.setProperty('--motion-page-ms', '0ms');
  animLog.length = 0;
  syncTourCompanion({ page: 'studio', tour: { page: 'studio', step: 0 }, motion: { mode: 'off', pageMs: 700, smallMs: 250 } }, T, { lang: 'en', doc: fakeDoc });
  assert.equal(animLog.length, 0, 'Off mode triggers zero animations');

  // Window resize to 1180x700 clamps bubble synchronously inside new viewport with zero jump.
  targetsBySelector['.scn-nav'] = makeEl('scn-nav', { left: 920, top: 640, width: 240, height: 42 });
  vw = 1180;
  vh = 700;
  assert.equal(typeof listeners.get('resize'), 'function', 'the companion registered a window resize listener');
  listeners.get('resize')();
  const resizedPos = parseTranslate3d(posRef.style.transform);
  assert.ok(resizedPos.x >= 16 && resizedPos.x + 328 <= 1180 - 16, `resized bx=${resizedPos.x} fits 1180w`);
  assert.ok(resizedPos.y >= 52 && resizedPos.y + 176 <= 700 - 16, `resized by=${resizedPos.y} fits 700h`);

  teardownTourCompanion(fakeDoc);
  assert.equal(layer.hidden, true);
});
