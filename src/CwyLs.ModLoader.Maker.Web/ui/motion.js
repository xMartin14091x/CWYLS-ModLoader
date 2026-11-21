// Scene current (F) motion system for Pack Maker Studio v2.
// Owns page-tab Scene F camera pan + staggered water-current transitions, Motion settings
// persistence (Off / Reduced / Full, Page flow 300-1000 ms, Small UI 150-400 ms), and
// CSS variables --motion-page-ms / --motion-small-ms per MOTION-CONTRACT.md.
import { esc } from './util.js';

export const MOTION_STORAGE_KEY = 'cwyls-maker-motion';
export const TAB_ORDER = ['studio', 'lines', 'packs', 'export', 'settings'];
export const MOTION_MODES = ['off', 'reduced', 'full'];

export const PAGE_MS_MIN = 300;
export const PAGE_MS_MAX = 1000;
export const PAGE_MS_DEFAULT = 700;

export const SMALL_MS_MIN = 150;
export const SMALL_MS_MAX = 400;
export const SMALL_MS_DEFAULT = 250;

const PARALLAX_STEP_PX = -132;
const PARALLAX_PLANES = [
  { key: 'sky', speed: 0.16, select: (doc) => doc?.querySelector?.('#sky .sky-drift') || null },
  { key: 'stars', speed: 0.31, select: (doc) => doc?.getElementById?.('stars') || null },
  { key: 'hillFar', speed: 0.58, select: (doc) => doc?.getElementById?.('hills') || null },
  { key: 'hillNear', speed: 0.96, select: (doc) => doc?.querySelector?.('#sky .hill-near') || null },
  { key: 'house', speed: 0.96, select: (doc) => doc?.querySelector?.('#ground .house') || null }
];

const planeOffsets = new Map(PARALLAX_PLANES.map((p) => [p.key, 0]));

let currentSettings = {
  mode: 'full',
  pageMs: PAGE_MS_DEFAULT,
  smallMs: SMALL_MS_DEFAULT
};

let activeTransition = null;
let transitionEpoch = 0;
let activeParallaxAnims = [];
let cachedShadowSheet = null;

export function clampPageMs(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return PAGE_MS_DEFAULT;
  return Math.min(PAGE_MS_MAX, Math.max(PAGE_MS_MIN, Math.round(n)));
}

export function clampSmallMs(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return SMALL_MS_DEFAULT;
  return Math.min(SMALL_MS_MAX, Math.max(SMALL_MS_MIN, Math.round(n)));
}

function detectPrefersReduced(matchMediaFn = globalThis.matchMedia) {
  try {
    return Boolean(typeof matchMediaFn === 'function' && matchMediaFn('(prefers-reduced-motion: reduce)')?.matches);
  } catch {
    return false;
  }
}

export function normalizeMotionSettings(raw, { prefersReduced = false } = {}) {
  const fallbackMode = prefersReduced ? 'reduced' : 'full';
  const mode = MOTION_MODES.includes(raw?.mode) ? raw.mode : fallbackMode;
  return {
    mode,
    pageMs: clampPageMs(raw?.pageMs),
    smallMs: clampSmallMs(raw?.smallMs)
  };
}

export function loadMotionSettings(storage = globalThis.localStorage, matchMediaFn = globalThis.matchMedia) {
  const prefersReduced = detectPrefersReduced(matchMediaFn);
  let parsed = null;
  try {
    const raw = storage?.getItem?.(MOTION_STORAGE_KEY);
    if (raw) parsed = JSON.parse(raw);
  } catch {
    parsed = null;
  }
  return normalizeMotionSettings(parsed, { prefersReduced });
}

export function saveMotionSettings(settings, storage = globalThis.localStorage) {
  const clean = normalizeMotionSettings(settings);
  try {
    storage?.setItem?.(MOTION_STORAGE_KEY, JSON.stringify(clean));
  } catch {
    /* private / sandboxed storage is optional */
  }
  return clean;
}

export function effectiveDurations(settings = currentSettings) {
  const s = normalizeMotionSettings(settings);
  if (s.mode === 'off') return { pageMs: 0, smallMs: 0 };
  if (s.mode === 'reduced') {
    return {
      pageMs: Math.max(120, Math.min(240, Math.round(s.pageMs * 0.34))),
      smallMs: Math.max(110, Math.min(200, Math.round(s.smallMs * 0.72)))
    };
  }
  return { pageMs: s.pageMs, smallMs: s.smallMs };
}

export function applyMotionSettings(settings, doc = globalThis.document) {
  const next = normalizeMotionSettings(settings);
  currentSettings = next;
  const eff = effectiveDurations(next);
  const root = doc?.documentElement;
  if (root) {
    if (root.dataset) root.dataset.motionMode = next.mode;
    root.setAttribute?.('data-motion-mode', next.mode);
    root.style?.setProperty?.('--motion-page-ms', `${eff.pageMs}ms`);
    root.style?.setProperty?.('--motion-small-ms', `${eff.smallMs}ms`);
  }
  return next;
}

export function getMotionSettings() {
  return { ...currentSettings };
}

export function setMotionSettings(patch, { storage = globalThis.localStorage, doc = globalThis.document } = {}) {
  const merged = normalizeMotionSettings({ ...currentSettings, ...(patch || {}) });
  saveMotionSettings(merged, storage);
  return applyMotionSettings(merged, doc);
}

export function initMotion({
  storage = globalThis.localStorage,
  doc = globalThis.document,
  matchMediaFn = globalThis.matchMedia,
  initialPage = 'studio'
} = {}) {
  cachedShadowSheet = null;
  const loaded = loadMotionSettings(storage, matchMediaFn);
  applyMotionSettings(loaded, doc);
  syncParallaxToPage(initialPage, { doc });
  return loaded;
}

export function directionOf(fromPage, toPage) {
  const a = TAB_ORDER.indexOf(fromPage || 'studio');
  const b = TAB_ORDER.indexOf(toPage || 'studio');
  const i0 = a < 0 ? 0 : a;
  const i1 = b < 0 ? 0 : b;
  if (i0 === i1) return 0;
  return i1 > i0 ? 1 : -1;
}

export function getActiveTransition() {
  return activeTransition;
}

/* ---------------- Scene F Parallax Scenery Rig ---------------- */

function ensureParallaxRig(doc) {
  if (!doc?.getElementById) return;
  const sky = doc.getElementById('sky');
  if (!sky) return;

  if (!sky.querySelector('.sky-drift') && doc.createElement) {
    const drift = doc.createElement('div');
    drift.className = 'sky-drift';
    drift.setAttribute('aria-hidden', 'true');
    sky.insertBefore(drift, sky.firstChild);
  }

  const stars = doc.getElementById('stars');
  // renderSky() rewrites #stars untiled, so the marker is the tiles themselves, not a one-time flag.
  if (stars && stars.getAttribute) {
    const vb = (stars.getAttribute('viewBox') || '').trim().split(/\s+/).map(Number);
    const w = vb[2] || 0, h = vb[3] || 0;
    const raw = stars.innerHTML || '';
    if (w > 0 && h > 0 && raw && !raw.includes('stars-tile')) {
      stars.setAttribute('viewBox', `0 0 ${w * 3} ${h}`);
      stars.setAttribute('width', String(w * 3));
      stars.setAttribute('height', String(h));
      stars.style.left = `-${w}px`;
      stars.style.width = `${w * 3}px`;
      stars.innerHTML =
        `<g class="stars-tile" transform="translate(0,0)">${raw}</g>` +
        `<g class="stars-tile" transform="translate(${w},0)">${raw}</g>` +
        `<g class="stars-tile" transform="translate(${w * 2},0)">${raw}</g>`;
    }
  }

  const hills = doc.getElementById('hills');
  if (hills && hills.querySelectorAll) {
    const paths = hills.querySelectorAll(':scope > path');
    if (paths.length >= 2) {
      const vb = (hills.getAttribute('viewBox') || '').trim().split(/\s+/).map(Number);
      const w = vb[2] || 0, hh = vb[3] || 0;
      if (w > 0 && hh > 0) {
        const dFar = paths[0].getAttribute('d') || '';
        const fillFar = paths[0].getAttribute('fill') || '#2a1b45';
        const dNear = paths[1].getAttribute('d') || '';
        const fillNear = paths[1].getAttribute('fill') || '#140e2a';

        hills.classList.add('hill-far');
        hills.setAttribute('viewBox', `0 0 ${w * 3} ${hh}`);
        hills.style.left = `-${w}px`;
        hills.style.width = `${w * 3}px`;
        hills.innerHTML =
          `<g transform="translate(0,0)"><path fill="${fillFar}" d="${dFar}"/></g>` +
          `<g transform="translate(${w},0)"><path fill="${fillFar}" d="${dFar}"/></g>` +
          `<g transform="translate(${w * 2},0)"><path fill="${fillFar}" d="${dFar}"/></g>`;

        let near = sky.querySelector('.hill-near');
        if (!near) {
          near = doc.createElementNS('http://www.w3.org/2000/svg', 'svg');
          near.setAttribute('class', 'hills hill-near');
          near.setAttribute('preserveAspectRatio', 'none');
          near.setAttribute('aria-hidden', 'true');
          sky.appendChild(near);
        }
        near.setAttribute('viewBox', `0 0 ${w * 3} ${hh}`);
        near.style.left = `-${w}px`;
        near.style.width = `${w * 3}px`;
        near.style.height = `${hh}px`;
        near.innerHTML =
          `<g transform="translate(0,0)"><path fill="${fillNear}" d="${dNear}"/></g>` +
          `<g transform="translate(${w},0)"><path fill="${fillNear}" d="${dNear}"/></g>` +
          `<g transform="translate(${w * 2},0)"><path fill="${fillNear}" d="${dNear}"/></g>`;
      }
    }
  }
}

function cancelParallaxAnims() {
  while (activeParallaxAnims.length) {
    const a = activeParallaxAnims.pop();
    try { a.cancel(); } catch {}
  }
}

export function syncParallaxToPage(page, { doc = globalThis.document } = {}) {
  if (!doc?.getElementById) return;
  cancelParallaxAnims();
  ensureParallaxRig(doc);
  const idx = Math.max(0, TAB_ORDER.indexOf(page || 'studio'));
  const active = currentSettings.mode === 'full';
  for (const plane of PARALLAX_PLANES) {
    const el = plane.select(doc);
    const targetX = active ? idx * PARALLAX_STEP_PX * plane.speed : 0;
    planeOffsets.set(plane.key, targetX);
    if (el?.style) {
      el.style.transform = targetX === 0 ? 'translate3d(0px, 0, 0)' : `translate3d(${targetX.toFixed(2)}px, 0, 0)`;
    }
  }
}

function animateSceneParallax(toPage, durMs, doc) {
  ensureParallaxRig(doc);
  cancelParallaxAnims();
  const idx = Math.max(0, TAB_ORDER.indexOf(toPage || 'studio'));
  const sky = doc.getElementById('sky');
  const ground = doc.getElementById('ground');
  sky?.classList?.add('is-panning');
  ground?.classList?.add('is-panning');

  const anims = [];
  for (const plane of PARALLAX_PLANES) {
    const el = plane.select(doc);
    const fromX = planeOffsets.get(plane.key) || 0;
    const toX = idx * PARALLAX_STEP_PX * plane.speed;
    planeOffsets.set(plane.key, toX);
    if (!el) continue;
    if (typeof el.animate !== 'function' || durMs <= 0 || Math.abs(toX - fromX) < 0.2) {
      if (el.style) el.style.transform = `translate3d(${toX.toFixed(2)}px, 0, 0)`;
      continue;
    }
    const overX = toX + (toX - fromX) * 0.035;
    const anim = el.animate([
      { offset: 0, transform: `translate3d(${fromX.toFixed(2)}px, 0, 0)` },
      { offset: 0.82, transform: `translate3d(${overX.toFixed(2)}px, 0, 0)` },
      { offset: 1, transform: `translate3d(${toX.toFixed(2)}px, 0, 0)` }
    ], { duration: durMs, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' });
    el.style.transform = `translate3d(${toX.toFixed(2)}px, 0, 0)`;
    anims.push(anim);
  }
  activeParallaxAnims = anims;
  Promise.allSettled(anims.map((a) => a.finished)).then(() => {
    if (activeParallaxAnims === anims) {
      sky?.classList?.remove('is-panning');
      ground?.classList?.remove('is-panning');
    }
  });
  return anims;
}

/* ---------------- Outgoing Snapshot & Stream Selection ---------------- */

function buildShadowSheet(doc) {
  if (cachedShadowSheet) return cachedShadowSheet;
  if (typeof CSSStyleSheet === 'undefined' || !doc?.styleSheets) return null;
  try {
    const chunks = [];
    const visit = (sheet) => {
      if (!sheet) return;
      let rules;
      try { rules = sheet.cssRules; } catch { return; }
      for (const rule of rules) {
        if (rule.type === 3 && rule.styleSheet) visit(rule.styleSheet);
        else if (rule.type !== 5) chunks.push(rule.cssText); // skip @font-face (document-global)
      }
    };
    for (const s of doc.styleSheets) visit(s);
    chunks.push(
      ':host{position:absolute;inset:0;pointer-events:none;overflow:hidden;display:block}' +
      '.out-stage{position:relative;width:100%;height:100%;pointer-events:none}' +
      '.out-item{position:absolute;margin:0 !important;will-change:transform,opacity;pointer-events:none}' +
      '.out-item>*{width:100%;height:100%;box-sizing:border-box;margin:0 !important}' +
      '.panel,.panel-soft{backdrop-filter:none !important;-webkit-backdrop-filter:none !important;background:rgba(15,19,50,.82)}'
    );
    const sheet = new CSSStyleSheet();
    sheet.replaceSync(chunks.join('\n'));
    cachedShadowSheet = sheet;
    return sheet;
  } catch {
    return null;
  }
}

function queryStreamTargets(mainEl, page) {
  if (!mainEl?.querySelectorAll) return [];
  const p = page || 'studio';
  let list = [];
  if (p === 'studio') {
    list = [
      ...mainEl.querySelectorAll('#banner:not([hidden]), #left .scn-nav, #left #vlist, #card, #studio-inspector:not([hidden])')
    ];
    if (!list.length) list = [...mainEl.querySelectorAll('#left:not([hidden]), #studio-card:not([hidden])')];
  } else if (p === 'lines') {
    list = [
      ...mainEl.querySelectorAll('#banner:not([hidden]), #page-view .vb-bar-row, #page-view .vb-table')
    ];
  } else if (p === 'packs') {
    list = [
      ...mainEl.querySelectorAll('#banner:not([hidden]), #page-view .pg-head, #page-view .packs-wins-note, #page-view .m-subhead, #page-view .pcard, #page-view .m-newpack')
    ];
  } else if (p === 'export') {
    list = [
      ...mainEl.querySelectorAll('#banner:not([hidden]), #page-view .pg-head, #page-view .exp-top, #page-view .exp-left, #page-view .exp-right, #page-view .exp-empty')
    ];
  } else if (p === 'settings') {
    list = [
      ...mainEl.querySelectorAll('#banner:not([hidden]), #page-view .pg-head, #page-view .st-voice, #page-view .st-workflow')
    ];
  }
  if (!list.length) {
    list = [...mainEl.querySelectorAll('#page-view:not([hidden]) > *')];
  }
  return list.slice(0, 10);
}

function cloneCompactBlock(el) {
  // Avoid cloning thousands of offscreen rows in Voice lines (.vb-tbody has up to 2,169 rows),
  // and skip virtual-list [aria-hidden="true"] spacers + overscan rows scrolled above scrollTop.
  if (el.classList?.contains('vb-table') || el.classList?.contains('vb-tbody')) {
    const copyVisibleRows = (src, dst) => {
      const children = [...(src.children || [])];
      let spacerTop = 0;
      if (children[0]?.getAttribute?.('aria-hidden') === 'true') {
        spacerTop = children[0].offsetHeight || parseFloat(children[0].style?.height || '') || 0;
      }
      const hiddenAbove = Math.max(0, (src.scrollTop || 0) - spacerTop);
      let skippedPx = 0;
      let count = 0;
      for (const row of children) {
        if (row.getAttribute?.('aria-hidden') === 'true') continue;
        const rowH = row.offsetHeight || 0;
        if (hiddenAbove > 0 && rowH > 0 && skippedPx + rowH <= hiddenAbove) {
          skippedPx += rowH;
          continue;
        }
        dst.appendChild(row.cloneNode(true));
        if (++count >= 16) break;
      }
    };
    const shell = el.cloneNode(false);
    if (el.classList?.contains('vb-tbody')) {
      copyVisibleRows(el, shell);
    } else {
      for (const child of el.children || []) {
        if (child.classList?.contains('vb-tbody')) {
          const tbody = child.cloneNode(false);
          copyVisibleRows(child, tbody);
          shell.appendChild(tbody);
        } else {
          shell.appendChild(child.cloneNode(true));
        }
      }
    }
    for (const node of [shell, ...(shell.querySelectorAll?.('[id],[data-act],[data-in],[data-ch],[name]') || [])]) {
      node.removeAttribute?.('id');
      node.removeAttribute?.('data-act');
      node.removeAttribute?.('data-in');
      node.removeAttribute?.('data-ch');
      node.removeAttribute?.('name');
    }
    return shell;
  }
  const clone = el.cloneNode(true);
  // Sync live form values into the visual clone.
  const srcFields = el.querySelectorAll?.('textarea, input, select') || [];
  const dstFields = clone.querySelectorAll?.('textarea, input, select') || [];
  for (let i = 0; i < srcFields.length && i < dstFields.length; i++) {
    const sf = srcFields[i], df = dstFields[i];
    if (sf.tagName === 'TEXTAREA' || sf.tagName === 'INPUT') {
      df.value = sf.value;
      if (sf.type === 'checkbox' || sf.type === 'radio') df.checked = sf.checked;
    } else if (sf.tagName === 'SELECT') {
      df.selectedIndex = sf.selectedIndex;
    }
  }
  // Strip interactive/id attributes inside clone for hygiene.
  for (const node of [clone, ...(clone.querySelectorAll?.('[id],[data-act],[data-in],[data-ch],[name]') || [])]) {
    node.removeAttribute?.('id');
    node.removeAttribute?.('data-act');
    node.removeAttribute?.('data-in');
    node.removeAttribute?.('data-ch');
    node.removeAttribute?.('name');
  }
  return clone;
}

function captureOutgoingSnapshots(mainEl, fromPage, flowSign) {
  if (!mainEl?.getBoundingClientRect) return [];
  const mr = mainEl.getBoundingClientRect();
  if (mr.width <= 0 || mr.height <= 0) return [];
  const rawEls = queryStreamTargets(mainEl, fromPage);
  const measured = [];
  for (const el of rawEls) {
    if (!el || el.hidden) continue;
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) continue;
    if (r.bottom < mr.top || r.top > mr.bottom) continue;
    measured.push({
      x: r.left - mr.left,
      y: r.top - mr.top,
      w: r.width,
      h: r.height,
      score: (flowSign < 0 ? r.left : -r.right) + r.top * 0.42,
      clone: cloneCompactBlock(el)
    });
  }
  measured.sort((a, b) => a.score - b.score);
  return measured;
}

function measureIncomingTargets(mainEl, toPage, flowSign) {
  const rawEls = queryStreamTargets(mainEl, toPage);
  const measured = [];
  for (const el of rawEls) {
    if (!el || el.hidden) continue;
    const r = el.getBoundingClientRect?.();
    if (!r || r.width <= 0 || r.height <= 0) continue;
    measured.push({
      el,
      score: (flowSign < 0 ? r.left : -r.right) + r.top * 0.42
    });
  }
  measured.sort((a, b) => a.score - b.score);
  return measured.map((m) => m.el);
}

/* ---------------- Scene F Keyframes (transform & opacity ONLY) ---------------- */

function buildOutKeyframes(flowSign, idx) {
  const dist = (275 + (idx % 4) * 28) * flowSign;
  const cy = 11 * (idx % 2 === 0 ? -1 : 1);
  return [
    { offset: 0, opacity: 1, transform: 'translate3d(0px, 0px, 0)' },
    { offset: 0.36, opacity: 0.84, transform: `translate3d(${(dist * 0.36).toFixed(1)}px, ${cy}px, 0)` },
    { offset: 0.75, opacity: 0.32, transform: `translate3d(${(dist * 0.78).toFixed(1)}px, ${(-cy * 0.55).toFixed(1)}px, 0)` },
    { offset: 1, opacity: 0, transform: `translate3d(${(dist * 1.06).toFixed(1)}px, 0px, 0)` }
  ];
}

function buildInKeyframes(flowSign, idx) {
  const startX = -flowSign * (290 + (idx % 4) * 30);
  const overX = flowSign * 8.0;
  const reboundX = -flowSign * 2.2;
  const cy = 12 * (idx % 2 === 0 ? 1 : -1);
  return [
    { offset: 0, opacity: 0, transform: `translate3d(${startX.toFixed(1)}px, ${cy}px, 0)` },
    { offset: 0.38, opacity: 0.88, transform: `translate3d(${(startX * 0.38).toFixed(1)}px, ${(-cy * 0.6).toFixed(1)}px, 0)` },
    { offset: 0.75, opacity: 1, transform: `translate3d(${overX.toFixed(1)}px, 1.5px, 0)` },
    { offset: 0.90, opacity: 1, transform: `translate3d(${reboundX.toFixed(1)}px, -0.6px, 0)` },
    { offset: 1, opacity: 1, transform: 'translate3d(0px, 0px, 0)' }
  ];
}

function spawnStreamRibbons(mainEl, mainWidth, mainHeight, flowSign, durMs, doc) {
  let box = mainEl.querySelector(':scope > .motion-ribbons');
  if (!box) {
    if (!doc?.createElement) return null;
    box = doc.createElement('div');
    box.className = 'motion-ribbons';
    box.setAttribute('aria-hidden', 'true');
    mainEl.appendChild(box);
  }
  const w = Math.max(760, Math.round(mainWidth || 1180));
  const h = Math.max(380, Math.round(mainHeight || 600));
  let paths = '';
  for (let i = 0; i < 4; i++) {
    const y = Math.round(h * (0.2 + i * 0.2));
    const amp = i % 2 === 0 ? 18 : -18;
    const stroke = i % 2 === 0 ? 'rgba(255,215,135,0.24)' : 'rgba(239,167,134,0.20)';
    paths += `<path d="M -160 ${y} C ${Math.round(w * 0.3)} ${y + amp}, ${Math.round(w * 0.7)} ${y - amp}, ${w + 160} ${y}" fill="none" stroke="${stroke}" stroke-width="${i % 2 === 0 ? 1.8 : 1.3}" stroke-dasharray="130 88" stroke-linecap="round"/>`;
  }
  box.innerHTML = `<svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none">${paths}</svg>`;
  if (typeof box.animate !== 'function') return null;
  const shift = flowSign * 200;
  return box.animate([
    { offset: 0, opacity: 0, transform: `translate3d(${(-shift * 0.35).toFixed(1)}px, 0, 0)` },
    { offset: 0.28, opacity: 0.92 },
    { offset: 0.74, opacity: 0.8 },
    { offset: 1, opacity: 0, transform: `translate3d(${(shift * 0.65).toFixed(1)}px, 0, 0)` }
  ], { duration: durMs, easing: 'cubic-bezier(0.25, 0.9, 0.35, 1)' });
}

const nowMs = () => (typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now());

function startIncomingAnim(el, idx, totalCount, st) {
  if (typeof el?.animate !== 'function') return null;
  el.classList?.add('motion-stream-active');
  if (st.mode === 'reduced') {
    return el.animate([{ opacity: 0 }, { opacity: 1 }], {
      duration: st.durationMs,
      easing: 'ease-out',
      fill: 'both'
    });
  }
  const inDenom = Math.max(1, totalCount - 1);
  const delay = Math.round((0.12 + 0.24 * (idx / inDenom)) * st.durationMs);
  const dur = Math.max(110, st.durationMs - delay);
  return el.animate(buildInKeyframes(st.flowSign, idx), {
    delay,
    duration: dur,
    easing: 'cubic-bezier(0.22, 1, 0.36, 1)',
    fill: 'both'
  });
}

function watchTransitionAnims(st) {
  const snap = [...st.anims];
  Promise.allSettled(snap.map((a) => a.finished)).then(() => {
    if (activeTransition !== st) return;
    if (st.anims.length !== snap.length || st.anims.some((a, i) => a !== snap[i])) {
      watchTransitionAnims(st);
      return;
    }
    cleanupActiveTransition();
  });
}

/**
 * Re-attaches mid-flight incoming WAAPI animations when `paint()` replaces `#page-view`
 * (or Studio zones) while async modal/pack data (`loadPacksDetails`, `runExportCheck`,
 * `loadVoiceDefault`, `loadPack`) resolves during an active transition.
 */
export function rebindIncomingStream(mainEl) {
  const st = activeTransition;
  if (!st) return;
  const host = mainEl || st.mainEl;
  if (!host || host !== st.mainEl) return;
  if (st.incomingEls.length > 0 && st.incomingEls.every((el) => el.isConnected !== false)) return;

  const elapsed = Math.max(0, nowMs() - st.startedAt);
  if (elapsed >= st.durationMs) return;

  const nextEls = measureIncomingTargets(host, st.to, st.flowSign);
  if (!nextEls.length) return;

  const nextSet = new Set(nextEls);
  const prevInMap = new Map();
  for (const a of st.inAnims || []) {
    const tgt = a?.effect?.target;
    if (tgt && tgt.isConnected !== false && nextSet.has(tgt)) {
      prevInMap.set(tgt, a);
    } else {
      try { a.cancel(); } catch {}
    }
  }
  for (const oldEl of st.incomingEls) {
    if (!nextSet.has(oldEl)) oldEl.classList?.remove('motion-stream-active');
  }

  const newInAnims = [];
  nextEls.forEach((el, idx) => {
    const existing = prevInMap.get(el);
    if (existing) {
      newInAnims.push(existing);
      return;
    }
    const anim = startIncomingAnim(el, idx, nextEls.length, st);
    if (anim) {
      try { anim.currentTime = elapsed; } catch {}
      newInAnims.push(anim);
    }
  });

  st.incomingEls = nextEls;
  st.inAnims = newInAnims;
  st.anims = [...st.baseAnims, ...newInAnims];
}

export function cleanupActiveTransition() {
  if (!activeTransition) return;
  const st = activeTransition;
  activeTransition = null;
  if (st.timer) clearTimeout(st.timer);
  for (const a of st.anims) {
    try { a.cancel(); } catch {}
  }
  for (const el of st.incomingEls) {
    el.classList?.remove('motion-stream-active');
  }
  st.outHost?.remove?.();
  if (st.ribbons) {
    st.ribbons.innerHTML = '';
    st.ribbons.style.opacity = '0';
  }
  st.mainEl?.classList?.remove('is-streaming');
  st.resolveFinished?.();
}

/**
 * Transitions from `fromPage` to `toPage`, invoking `apply()` to swap page content.
 * Per MOTION-CONTRACT.md:
 * - Direction follows tab order; top bar (#top) never moves.
 * - Off = 0 ms instant swap; Reduced = simple calm fade; Full = Scene F camera pan + staggered stream.
 * - Animates transform and opacity ONLY; zero layout reads inside animation frames.
 */
export async function transitionPage(fromPage, toPage, apply) {
  if (typeof apply !== 'function') return undefined;
  const myEpoch = ++transitionEpoch;
  const doc = globalThis.document;
  const from = fromPage || 'studio';
  const to = toPage || 'studio';
  const mode = currentSettings.mode;
  const mainEl = doc?.getElementById?.('main') || null;
  const isShot = Boolean(doc?.documentElement?.classList?.contains?.('shot'));

  cleanupActiveTransition();

  if (from === to || mode === 'off' || isShot || !mainEl || typeof mainEl.animate !== 'function') {
    const res = await apply();
    if (myEpoch !== transitionEpoch) return res;
    syncParallaxToPage(to, { doc });
    return res;
  }

  const navDir = directionOf(from, to) || 1;
  const flowSign = -navDir;

  // 1. Pre-swap layout read pass (single pass before DOM mutation).
  const mainRect = mainEl.getBoundingClientRect();
  const outSnapshots = captureOutgoingSnapshots(mainEl, from, flowSign);

  // 2. Swap live page content.
  const res = await apply();
  if (myEpoch !== transitionEpoch) return res;

  // 3. Post-swap layout read pass (single pass before any animation starts).
  const incomingEls = measureIncomingTargets(mainEl, to, flowSign);

  // 4. Mount outgoing visual clone layer inside an isolated ShadowRoot (zero light-DOM collisions).
  let outHost = null;
  let outStage = null;
  if (outSnapshots.length && doc.createElement) {
    outHost = doc.createElement('div');
    outHost.className = 'motion-out-layer';
    outHost.setAttribute('aria-hidden', 'true');
    const sr = outHost.attachShadow ? outHost.attachShadow({ mode: 'open' }) : outHost;
    const sheet = buildShadowSheet(doc);
    if (sheet && sr.adoptedStyleSheets !== undefined) {
      sr.adoptedStyleSheets = [sheet];
    }
    outStage = doc.createElement('div');
    outStage.className = 'out-stage';
    for (const snap of outSnapshots) {
      const wrap = doc.createElement('div');
      wrap.className = 'out-item';
      wrap.style.left = `${snap.x.toFixed(1)}px`;
      wrap.style.top = `${snap.y.toFixed(1)}px`;
      wrap.style.width = `${snap.w.toFixed(1)}px`;
      wrap.style.height = `${snap.h.toFixed(1)}px`;
      wrap.appendChild(snap.clone);
      snap.wrap = wrap;
      outStage.appendChild(wrap);
    }
    sr.appendChild(outStage);
    mainEl.appendChild(outHost);
  }

  const eff = effectiveDurations(currentSettings);
  const startedAt = nowMs();
  const baseAnims = [];
  const inAnims = [];

  if (mode === 'reduced') {
    syncParallaxToPage(to, { doc });
    const fadeMs = eff.pageMs;
    if (outStage?.animate) {
      baseAnims.push(outStage.animate([{ opacity: 1 }, { opacity: 0 }], { duration: fadeMs, easing: 'ease-out', fill: 'forwards' }));
    }
    let resolveFinished;
    const finished = new Promise((r) => { resolveFinished = r; });
    const st = { mode, from, to, flowSign, startedAt, durationMs: fadeMs, baseAnims, inAnims, anims: [], incomingEls, outHost, ribbons: null, mainEl, finished, resolveFinished, timer: null };
    incomingEls.forEach((el, idx) => {
      const anim = startIncomingAnim(el, idx, incomingEls.length, st);
      if (anim) inAnims.push(anim);
    });
    st.anims = [...baseAnims, ...inAnims];
    st.timer = setTimeout(() => { if (activeTransition === st) cleanupActiveTransition(); }, fadeMs + 32);
    activeTransition = st;
    watchTransitionAnims(st);
    return res;
  }

  // Full Scene F choreography: sky/stars/hills parallax camera pan + staggered stream + soft spring settle.
  const T = eff.pageMs;
  mainEl.classList.add('is-streaming');

  animateSceneParallax(to, T, doc);

  const ribbonAnim = spawnStreamRibbons(mainEl, mainRect.width, mainRect.height, flowSign, T, doc);
  if (ribbonAnim) baseAnims.push(ribbonAnim);

  const outDenom = Math.max(1, outSnapshots.length - 1);
  outSnapshots.forEach((snap, idx) => {
    if (!snap.wrap?.animate) return;
    const delay = Math.round(0.18 * T * (idx / outDenom));
    const dur = Math.max(90, Math.round(0.56 * T));
    baseAnims.push(snap.wrap.animate(buildOutKeyframes(flowSign, idx), {
      delay,
      duration: dur,
      easing: 'cubic-bezier(0.32, 0.04, 0.62, 0.96)',
      fill: 'both'
    }));
  });

  let resolveFinished;
  const finished = new Promise((r) => { resolveFinished = r; });
  const ribbons = mainEl.querySelector(':scope > .motion-ribbons');
  const st = { mode, from, to, flowSign, startedAt, durationMs: T, baseAnims, inAnims, anims: [], incomingEls, outHost, ribbons, mainEl, finished, resolveFinished, timer: null };
  incomingEls.forEach((el, idx) => {
    const anim = startIncomingAnim(el, idx, incomingEls.length, st);
    if (anim) inAnims.push(anim);
  });
  st.anims = [...baseAnims, ...inAnims];
  st.timer = setTimeout(() => { if (activeTransition === st) cleanupActiveTransition(); }, T + 48);
  activeTransition = st;
  watchTransitionAnims(st);
  return res;
}

/* ---------------- Settings Page Renderer (MOTION-CONTRACT.md) ---------------- */

export function renderMotionSettings(s, T) {
  const m = normalizeMotionSettings(s?.motion || currentSettings);
  const modes = [
    ['off', T('motion_off')],
    ['reduced', T('motion_reduced')],
    ['full', T('motion_full')]
  ];
  return `<div class="st-motion${m.mode === 'off' ? ' is-off' : ''}" data-motion-settings="1">` +
    `<div class="st-motion-head">` +
    `<div class="st-motion-title"><b>${esc(T('motion_title'))}</b><span>${esc(T('motion_hint'))}</span></div>` +
    `<div class="st-motion-seg" role="radiogroup" aria-label="${esc(T('motion_mode'))}">` +
    modes.map(([id, label]) =>
      `<button type="button" class="st-motion-b${m.mode === id ? ' on' : ''}" role="radio" aria-checked="${m.mode === id}" data-act="motion-mode" data-id="${id}">${esc(label)}</button>`
    ).join('') +
    `</div>` +
    `</div>` +
    `<div class="st-motion-sliders">` +
    `<label class="st-motion-sl">` +
    `<span class="st-motion-sl-top"><span>${esc(T('motion_page'))}</span><output class="st-motion-val" data-motion-out="page">${esc(T('motion_ms', { n: m.pageMs }))}</output></span>` +
    `<input type="range" min="${PAGE_MS_MIN}" max="${PAGE_MS_MAX}" step="25" value="${m.pageMs}" data-in="motion-page" aria-label="${esc(T('motion_page'))}">` +
    `</label>` +
    `<label class="st-motion-sl">` +
    `<span class="st-motion-sl-top"><span>${esc(T('motion_small'))}</span><output class="st-motion-val" data-motion-out="small">${esc(T('motion_ms', { n: m.smallMs }))}</output></span>` +
    `<input type="range" min="${SMALL_MS_MIN}" max="${SMALL_MS_MAX}" step="10" value="${m.smallMs}" data-in="motion-small" aria-label="${esc(T('motion_small'))}">` +
    `</label>` +
    `</div>` +
    `</div>`;
}
