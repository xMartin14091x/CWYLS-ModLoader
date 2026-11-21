import test from 'node:test';
import assert from 'node:assert/strict';
import th from '../../ui/i18n/th.js';
import { makeT } from '../../ui/i18n/i18n.js';
import {
  renderHelpPopover,
  syncTourCompanion,
  teardownTourCompanion
} from '../../ui/zones/tour.js';
import {
  initMotion,
  transitionPage,
  cleanupActiveTransition
} from '../../ui/motion.js';

const PAGES = ['studio', 'lines', 'packs', 'export', 'settings'];

test('HMC-1: renderHelpPopover includes Creator Guide link in EN and TH across all 5 pages', () => {
  const enT = makeT('en');
  const thT = makeT('th');
  for (const page of PAGES) {
    const enHtml = renderHelpPopover({ page }, enT, 'en');
    assert.match(
      enHtml,
      /<a\b[^>]*class="[^"]*help-guide-link[^"]*"[^>]*href="https:\/\/github\.com\/xMartin14091x\/CWYLS-ModLoader\/blob\/main\/docs\/guide\/pack-maker\.md"[^>]*target="_blank"[^>]*rel="noopener noreferrer"/,
      `EN ${page} help popover must link to docs/guide/pack-maker.md`
    );

    const thHtml = renderHelpPopover({ page }, thT, 'th');
    assert.match(
      thHtml,
      /<a\b[^>]*class="[^"]*help-guide-link[^"]*"[^>]*href="https:\/\/github\.com\/xMartin14091x\/CWYLS-ModLoader\/blob\/main\/docs\/guide\/pack-maker\.th\.md"[^>]*target="_blank"[^>]*rel="noopener noreferrer"/,
      `TH ${page} help popover must link to docs/guide/pack-maker.th.md`
    );
  }
});

test('HMC-5: Thai "?" Help Popover uses "บทพูด" and never stale "บรรทัดเสียง"', () => {
  const thT = makeT('th');
  for (const [k, v] of Object.entries(th)) {
    if (typeof v === 'string') {
      assert.doesNotMatch(v, /บรรทัดเสียง/, `th.${k} must not contain stale tab name "บรรทัดเสียง"`);
    }
  }
  const studioHelp = renderHelpPopover({ page: 'studio' }, thT, 'th');
  const packsHelp = renderHelpPopover({ page: 'packs' }, thT, 'th');
  assert.match(studioHelp, /บทพูด/);
  assert.match(packsHelp, /บทพูด/);
});

test('HMCM P2-4 (unit): syncTourCompanion focuses bubble on open, preserves [data-act] focus across outerHTML step swaps, and restores "?" on teardown', () => {
  let activeEl = { id: 'body' };
  const makeBtn = (act, disabled = false) => ({
    dataset: { act },
    disabled,
    closest: (sel) => (sel === '#tour-card' || sel === '#tour-layer' ? true : null),
    focus() { if (!this.disabled) activeEl = this; }
  });
  const helpBtn = {
    dataset: { act: 'keys-menu' },
    focus() { activeEl = this; }
  };

  let buttons = new Map();
  const rebuildButtons = (html) => {
    buttons = new Map();
    const prevDisabled = /data-act="tour-prev"\s+disabled/.test(html);
    buttons.set('tour-prev', makeBtn('tour-prev', prevDisabled));
    buttons.set('tour-next', makeBtn('tour-next', false));
    buttons.set('tour-skip', makeBtn('tour-skip', false));
  };

  let htmlBuf = '';
  const cardEl = {
    id: 'tour-card',
    dataset: {},
    offsetWidth: 328,
    offsetHeight: 176,
    classList: { add() {}, remove() {}, contains: () => false },
    setAttribute() {},
    getBoundingClientRect: () => ({ left: 40, top: 80, width: 328, height: 176 }),
    contains: (el) => [...buttons.values()].includes(el),
    querySelector: (sel) => {
      const m = sel.match(/\[data-act="([^"]+)"\](:not\(\[disabled\]\))?/);
      if (!m) return null;
      const btn = buttons.get(m[1]) || null;
      if (btn && m[2] && btn.disabled) return null;
      return btn;
    }
  };
  const posEl = {
    id: 'tour-pos',
    style: { transform: '' },
    getBoundingClientRect: () => ({ left: 40, top: 80, width: 328, height: 176 })
  };
  const tailEl = { id: 'tour-tail', style: { transform: '' } };
  const cursorEl = {
    id: 'tour-cursor',
    style: { transform: '' },
    getBoundingClientRect: () => ({ left: 20, top: 60, width: 36, height: 40 })
  };
  const rippleEl = { id: 'tour-ripple', style: {} };
  const makeContentNode = () => ({
    id: 'tour-content',
    get outerHTML() { return htmlBuf; },
    set outerHTML(next) {
      htmlBuf = next;
      rebuildButtons(next);
    }
  });
  let contentNode = makeContentNode();

  const layer = {
    id: 'tour-layer',
    hidden: true,
    contains: (el) => [...buttons.values()].includes(el),
    get innerHTML() { return htmlBuf; },
    set innerHTML(val) {
      htmlBuf = val;
      if (val) {
        rebuildButtons(val);
        contentNode = makeContentNode();
      } else {
        buttons = new Map();
      }
    }
  };

  const root = {
    classList: { contains: () => false },
    getAttribute: () => 'off',
    style: { getPropertyValue: () => '0ms' }
  };
  const target = {
    offsetWidth: 240,
    offsetHeight: 42,
    getBoundingClientRect: () => ({ left: 16, top: 64, width: 240, height: 42 })
  };
  const fakeDoc = {
    documentElement: root,
    get activeElement() { return activeEl; },
    defaultView: { innerWidth: 1180, innerHeight: 700, scrollX: 0, scrollY: 0 },
    addEventListener() {},
    removeEventListener() {},
    getElementById: (id) => ({
      'tour-layer': layer,
      'tour-pos': posEl,
      'tour-card': cardEl,
      'tour-tail': tailEl,
      'tour-cursor': cursorEl,
      'tour-ripple': rippleEl,
      'tour-content': contentNode
    }[id] || null),
    querySelector: (sel) => (sel === '[data-act="keys-menu"]' ? helpBtn : cardEl.querySelector(sel)),
    querySelectorAll: () => [target]
  };

  const T = makeT('en');
  syncTourCompanion({ page: 'studio', tour: { page: 'studio', step: 0 }, motion: { mode: 'off' } }, T, { lang: 'en', doc: fakeDoc });
  assert.equal(activeEl, buttons.get('tour-next'), 'Opening companion focuses Next button');

  // Step 0 -> Step 1 while Next is focused: new Next button receives focus after outerHTML replacement.
  const step0Next = activeEl;
  syncTourCompanion({ page: 'studio', tour: { page: 'studio', step: 1 }, motion: { mode: 'off' } }, T, { lang: 'en', doc: fakeDoc });
  assert.notEqual(buttons.get('tour-next'), step0Next, 'outerHTML created a fresh Next button');
  assert.equal(activeEl, buttons.get('tour-next'), 'Step swap refocuses new Next button');

  // Focus Back on Step 1 -> Step 0 (where Back becomes disabled): falls back to Next instead of dropping to body.
  buttons.get('tour-prev').focus();
  syncTourCompanion({ page: 'studio', tour: { page: 'studio', step: 0 }, motion: { mode: 'off' } }, T, { lang: 'en', doc: fakeDoc });
  assert.equal(activeEl, buttons.get('tour-next'), 'Disabled Back on step 0 falls back to Next');

  // Closing companion while focus is inside #tour-layer restores focus to "?" ([data-act="keys-menu"]).
  syncTourCompanion({ page: 'studio', tour: null, motion: { mode: 'off' } }, T, { lang: 'en', doc: fakeDoc });
  assert.equal(activeEl, helpBtn, 'Closing companion restores focus to "?" button');
  teardownTourCompanion(fakeDoc);
});

test('HMCM P2-5 (unit): transitionPage drops an in-flight async apply() when a newer transition starts', async () => {
  const prevDoc = globalThis.document;
  const appended = [];
  const makeAnimEl = (id, rect = { left: 0, top: 50, width: 400, height: 300 }) => ({
    id,
    hidden: false,
    offsetWidth: rect.width,
    offsetHeight: rect.height,
    style: {},
    classList: {
      set: new Set(),
      add(c) { this.set.add(c); },
      remove(c) { this.set.delete(c); },
      contains(c) { return this.set.has(c); }
    },
    getBoundingClientRect: () => rect,
    cloneNode: () => makeAnimEl(`${id}-clone`, rect),
    querySelectorAll: () => [],
    querySelector: () => null,
    setAttribute() {},
    removeAttribute() {},
    appendChild(ch) { appended.push(ch); ch.parentNode = this; return ch; },
    remove() { const idx = appended.indexOf(this); if (idx >= 0) appended.splice(idx, 1); },
    animate: () => ({ cancel() {}, finished: new Promise(() => {}) })
  });

  const blockA = makeAnimEl('block-a');
  const mainEl = makeAnimEl('main', { left: 0, top: 48, width: 1180, height: 652 });
  mainEl.querySelectorAll = () => [blockA];

  const vars = new Map();
  const fakeDoc = {
    documentElement: {
      dataset: {},
      classList: { contains: () => false },
      setAttribute() {},
      getAttribute: () => 'full',
      style: {
        setProperty: (k, v) => vars.set(k, String(v)),
        getPropertyValue: (k) => vars.get(k) || ''
      }
    },
    getElementById: (id) => (id === 'main' ? mainEl : null),
    querySelector: () => null,
    styleSheets: [],
    createElement: (tag) => {
      const node = makeAnimEl(tag);
      node.className = '';
      return node;
    }
  };

  globalThis.document = fakeDoc;
  try {
    initMotion({
      storage: { getItem: () => null, setItem() {} },
      doc: fakeDoc,
      matchMediaFn: () => ({ matches: false })
    });

    let releaseSlowApply;
    const slowGate = new Promise((r) => { releaseSlowApply = r; });

    // Transition #1 ('studio' -> 'export') blocks inside async apply().
    const t1 = transitionPage('studio', 'export', async () => {
      await slowGate;
      return 'stale-export';
    });

    // While #1 is waiting, user clicks 'settings' -> Transition #2 runs synchronously and mounts its out-layer.
    await transitionPage('export', 'settings', () => 'fresh-settings');
    const outLayersAfterT2 = appended.filter((n) => n.className === 'motion-out-layer');
    assert.equal(outLayersAfterT2.length, 1, 'Transition #2 mounted exactly one outgoing layer');

    // Now Transition #1's slow apply() finally resolves — it MUST be dropped and NOT mount a second stale out-layer.
    releaseSlowApply();
    await t1;
    const outLayersAfterT1 = appended.filter((n) => n.className === 'motion-out-layer');
    assert.equal(outLayersAfterT1.length, 1, 'Stale Transition #1 must not mount an old-page clone after #2 started');
    assert.equal(outLayersAfterT1[0], outLayersAfterT2[0], 'Active outgoing layer still belongs to Transition #2');
  } finally {
    cleanupActiveTransition();
    globalThis.document = prevDoc;
  }
});
