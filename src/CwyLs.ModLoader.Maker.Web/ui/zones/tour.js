// Page-aware help and an opt-in spotlight guide (D-124 / D-126).
// Remembers seen tours per page in localStorage (`cwyls-maker-tours-seen`, wrapped in try/catch).
import { esc } from '../util.js';
import { I } from './icons.js';
import { openPop } from './dropdown.js';
import { effectiveDurations } from '../motion.js';
import { helpPage, PAGE_TERMS } from './guide-context.js';

export const TOUR_STORAGE_KEY = 'cwyls-maker-tours-seen';

export function loadSeenTours(storage = globalThis.localStorage) {
  try {
    const raw = storage?.getItem(TOUR_STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

export function isPageTourSeen(page, storage = globalThis.localStorage) {
  const seen = loadSeenTours(storage);
  return Boolean(seen[page || 'studio']);
}

export function markPageTourSeen(page, storage = globalThis.localStorage) {
  try {
    const seen = loadSeenTours(storage);
    seen[page || 'studio'] = true;
    storage?.setItem(TOUR_STORAGE_KEY, JSON.stringify(seen));
    return seen;
  } catch {
    return {};
  }
}

export function resetSeenTours(storage = globalThis.localStorage) {
  try {
    storage?.removeItem(TOUR_STORAGE_KEY);
  } catch {
    // ignore storage errors
  }
}

export const PAGE_HELP = {
  newpack: {
    steps: ['help_newpack_s1', 'help_newpack_s2', 'help_newpack_s3'],
    connect: 'help_newpack_connect',
    keys: [['Esc', 'k_esc']]
  },
  studio: {
    steps: ['help_studio_s1', 'help_studio_s2', 'help_studio_s3', 'help_studio_s4'],
    connect: 'help_studio_connect',
    keys: [
      ['↑ / ↓', 'k_line'],
      ['↵ / ⌘↵', 'k_done'],
      ['R', 'k_rec'],
      ['Space', 'k_play'],
      ['T', 'k_game'],
      ['⇧T', 'k_orig'],
      ['Esc', 'k_esc']
    ]
  },
  lines: {
    steps: ['help_lines_s1', 'help_lines_s2', 'help_lines_s3'],
    connect: 'help_lines_connect',
    keys: [
      ['Esc', 'k_esc'],
      ['↑ / ↓', 'k_line']
    ]
  },
  packs: {
    steps: ['help_packs_s1', 'help_packs_s2', 'help_packs_s3'],
    connect: 'help_packs_connect',
    keys: [
      ['Esc', 'k_esc']
    ]
  },
  export: {
    steps: ['help_export_s1', 'help_export_s2', 'help_export_s3'],
    connect: 'help_export_connect',
    keys: [
      ['Esc', 'k_esc']
    ]
  },
  settings: {
    steps: ['help_settings_s1', 'help_settings_s2', 'help_settings_s3'],
    connect: 'help_settings_connect',
    keys: [
      ['Esc', 'k_esc']
    ]
  }
};

export const TOUR_STEPS = {
  newpack: [
    ['name', '.m-newpack .np-row2', 1],
    ['language', '.m-newpack .np-row3', 2],
    ['author', '.m-newpack .np-col > .np-row2:last-child', 3],
    ['icon', '.m-newpack .np-icon-box', 4],
    ['create', '.m-newpack .np-b', 5]
  ].map(([id, selector, n]) => ({ id: `newpack-${id}`, selector, preferSide: 'below', titleKey: `tour_newpack_${n}_t`, whatKey: `tour_newpack_${n}_w`, bodyKey: `tour_newpack_${n}_b` })),
  studio: [
    {
      id: 'studio-scene',
      selector: '.scn-nav',
      fallback: '#left',
      preferSide: 'right',
      anchor: { rx: 0.78, ry: 0.54 },
      titleKey: 'tour_studio_1_t',
      whatKey: 'tour_studio_1_w',
      bodyKey: 'tour_studio_1_b'
    },
    {
      id: 'studio-editor',
      selector: '#ed',
      fallback: '#card',
      preferSide: 'below',
      anchor: { rx: 0.24, ry: 0.52 },
      titleKey: 'tour_studio_2_t',
      whatKey: 'tour_studio_2_w',
      bodyKey: 'tour_studio_2_b'
    },
    {
      id: 'studio-deck',
      selector: '#deck-body .d-mic, #deck-body .d-nomic',
      fallback: '#deck-body',
      preferSide: 'right',
      anchor: { rx: 0.56, ry: 0.46 },
      titleKey: 'tour_studio_3_t',
      whatKey: 'tour_studio_3_w',
      bodyKey: 'tour_studio_3_b'
    },
    {
      id: 'studio-origin',
      selector: '#deck-body .d-meta',
      preferSide: 'left',
      titleKey: 'tour_studio_origin_t',
      whatKey: 'tour_studio_origin_w',
      bodyKey: 'tour_studio_origin_b'
    },
    {
      id: 'studio-hero',
      selector: '#hero .hb-mine',
      fallback: '#hero .hero-b, .card-bottom',
      preferSide: 'above',
      anchor: { rx: 0.52, ry: 0.45 },
      titleKey: 'tour_studio_4_t',
      whatKey: 'tour_studio_4_w',
      bodyKey: 'tour_studio_4_b'
    }
  ],
  lines: [
    {
      id: 'lines-filters',
      selector: '.vb-bar-row:first-child',
      fallback: '.vb-bar',
      preferSide: 'below',
      anchor: { rx: 0.3, ry: 0.62 },
      titleKey: 'tour_lines_1_t',
      whatKey: 'tour_lines_1_w',
      bodyKey: 'tour_lines_1_b'
    },
    {
      id: 'lines-chips',
      selector: '.vb-chips-row .chips',
      fallback: '.vb-chips-row',
      preferSide: 'below',
      anchor: { rx: 0.26, ry: 0.58 },
      titleKey: 'tour_lines_2_t',
      whatKey: 'tour_lines_2_w',
      bodyKey: 'tour_lines_2_b'
    },
    {
      id: 'lines-row',
      selector: '#vb-list .vb-row',
      fallback: '.vb-table',
      preferSide: 'below',
      anchor: { rx: 0.34, ry: 0.56 },
      titleKey: 'tour_lines_3_t',
      whatKey: 'tour_lines_3_w',
      bodyKey: 'tour_lines_3_b'
    }
  ],
  packs: [
    {
      id: 'packs-tabs',
      selector: '.pg-subtabs',
      fallback: '.pg-head',
      preferSide: 'below',
      anchor: { rx: 0.52, ry: 0.64 },
      titleKey: 'tour_packs_1_t',
      whatKey: 'tour_packs_1_w',
      bodyKey: 'tour_packs_1_b'
    },
    {
      id: 'packs-progress',
      selector: '.pcard.cur .pcard-prog, .m-newpack .np-row2',
      fallback: '.packs-wins-note, .pg-body',
      preferSide: 'below',
      anchor: { rx: 0.32, ry: 0.55 },
      titleKey: 'tour_packs_2_t',
      whatKey: 'tour_packs_2_w',
      bodyKey: 'tour_packs_2_b'
    },
    {
      id: 'packs-actions',
      selector: '.pcard.cur .pcard-acts, .m-newpack [data-act="np-create"]',
      fallback: '.pcard .pcard-acts, .pg-body',
      preferSide: 'left',
      anchor: { rx: 0.42, ry: 0.54 },
      titleKey: 'tour_packs_3_t',
      whatKey: 'tour_packs_3_w',
      bodyKey: 'tour_packs_3_b'
    }
  ],
  export: [
    {
      id: 'export-summary',
      selector: '[data-act="export-check"]',
      fallback: '.exp-top, .pg-head',
      preferSide: 'left',
      anchor: { rx: 0.45, ry: 0.56 },
      titleKey: 'tour_export_1_t',
      whatKey: 'tour_export_1_w',
      bodyKey: 'tour_export_1_b'
    },
    {
      id: 'export-checks',
      selector: '.chk-list',
      fallback: '.exp-left, .m-export',
      preferSide: 'right',
      anchor: { rx: 0.82, ry: 0.26 },
      titleKey: 'tour_export_2_t',
      whatKey: 'tour_export_2_w',
      bodyKey: 'tour_export_2_b'
    },
    {
      id: 'export-build',
      selector: '[data-act="export-run"]',
      fallback: '.exp-right, .m-export',
      preferSide: 'left',
      anchor: { rx: 0.38, ry: 0.52 },
      titleKey: 'tour_export_3_t',
      whatKey: 'tour_export_3_w',
      bodyKey: 'tour_export_3_b'
    }
  ],
  settings: [
    {
      id: 'settings-voice',
      selector: '.st-voice .st-origin',
      fallback: '.st-voice, .m-settings',
      preferSide: 'below',
      anchor: { rx: 0.35, ry: 0.55 },
      titleKey: 'tour_settings_1_t',
      whatKey: 'tour_settings_1_w',
      bodyKey: 'tour_settings_1_b'
    },
    {
      id: 'settings-workflow',
      selector: '[data-act="reset-tours"]',
      fallback: '.st-tours-row, .st-workflow',
      preferSide: 'left',
      anchor: { rx: 0.45, ry: 0.52 },
      titleKey: 'tour_settings_2_t',
      whatKey: 'tour_settings_2_w',
      bodyKey: 'tour_settings_2_b'
    },
    {
      id: 'settings-motion',
      selector: '[data-motion-settings="1"] .st-motion-seg',
      fallback: '[data-motion-settings="1"], .st-workflow',
      preferSide: 'left',
      anchor: { rx: 0.48, ry: 0.52 },
      titleKey: 'tour_settings_3_t',
      whatKey: 'tour_settings_3_w',
      bodyKey: 'tour_settings_3_b'
    }
  ]
};

export function tourStepsFor(page) {
  return TOUR_STEPS[page] || TOUR_STEPS.studio;
}

export function clampTourStep(page, step = 0) {
  const steps = tourStepsFor(page);
  const raw = Number.isFinite(Number(step)) ? Math.trunc(Number(step)) : 0;
  const index = Math.max(0, Math.min(steps.length - 1, raw));
  return {
    steps,
    index,
    stepSpec: steps[index] || null,
    isFirst: index === 0,
    isLast: index >= steps.length - 1
  };
}

/** Pure step-state reducer for Next / Back / Skip. Returns next `{ page, step }` or `null` when finished. */
export function nextTourState(tour, action) {
  if (!tour) return null;
  const page = tour.page || 'studio';
  const { steps, index } = clampTourStep(page, tour.step);
  if (action === 'skip') return null;
  if (action === 'prev') {
    return index > 0 ? { page, step: index - 1 } : { page, step: 0 };
  }
  if (action === 'next') {
    return index + 1 < steps.length ? { page, step: index + 1 } : null;
  }
  return { page, step: index };
}

const CURSOR_TIP_OFFSET_X = 5;
const CURSOR_TIP_OFFSET_Y = 4;

function clamp(v, lo, hi) {
  if (hi < lo) return lo;
  return Math.max(lo, Math.min(hi, v));
}

function overlapArea(ax1, ay1, ax2, ay2, bx1, by1, bx2, by2) {
  const ox = Math.max(0, Math.min(ax2, bx2) - Math.max(ax1, bx1));
  const oy = Math.max(0, Math.min(ay2, by2) - Math.max(ay1, by1));
  return ox * oy;
}

function pointToBoxDistance(px, py, x1, y1, x2, y2) {
  const dx = px < x1 ? x1 - px : px > x2 ? px - x2 : 0;
  const dy = py < y1 ? y1 - py : py > y2 ? py - y2 : 0;
  return Math.hypot(dx, dy);
}

/**
 * Pure companion layout solver: places the guiding cursor tip on the visible part of `targetRect`
 * and places the text bubble beside it, strictly inside `[margin .. vw - margin, topSafe .. vh - margin]`
 * without covering the cursor. Tested across 1180x700 and 1440x900.
 */
export function computeCompanionLayout({
  targetRect,
  bubbleSize,
  viewport,
  preferSide = 'auto',
  anchor = null,
  topSafe = 52,
  margin = 16
} = {}) {
  const vw = Math.max(320, Number(viewport?.width) || 1180);
  const vh = Math.max(320, Number(viewport?.height) || 700);
  const safeTop = clamp(Number(topSafe) || 52, margin, Math.max(margin, vh - 160));
  const bw = Math.min(vw - margin * 2, Math.max(220, Math.round(Number(bubbleSize?.width) || 328)));
  const bh = Math.min(vh - safeTop - margin, Math.max(110, Math.round(Number(bubbleSize?.height) || 178)));

  const minBx = margin;
  const maxBx = Math.max(margin, vw - bw - margin);
  const minBy = safeTop;
  const maxBy = Math.max(safeTop, vh - bh - margin);

  const hasValidTarget = Boolean(
    targetRect &&
    Number.isFinite(targetRect.left) &&
    Number.isFinite(targetRect.top) &&
    targetRect.width > 0 &&
    targetRect.height > 0
  );

  if (!hasValidTarget) {
    const bx = clamp(Math.round((vw - bw) / 2), minBx, maxBx);
    const by = clamp(Math.round((vh - bh) / 2), minBy, maxBy);
    const tipX = clamp(bx - 18, 16, vw - 36);
    const tipY = clamp(by + 28, safeTop, vh - 40);
    return {
      hasTarget: false,
      side: 'right',
      bx,
      by,
      bw,
      bh,
      tipX,
      tipY,
      cx: Math.round(tipX - CURSOR_TIP_OFFSET_X),
      cy: Math.round(tipY - CURSOR_TIP_OFFSET_Y),
      tx: -6,
      ty: clamp(tipY - by - 6, 18, Math.max(18, bh - 30))
    };
  }

  const visLeft = clamp(targetRect.left, 8, vw - 28);
  const visRight = clamp(targetRect.left + targetRect.width, visLeft + 14, vw - 8);
  const visTop = clamp(targetRect.top, safeTop - 6, vh - 28);
  const visBottom = clamp(targetRect.top + targetRect.height, visTop + 14, vh - 8);
  const visW = Math.max(14, visRight - visLeft);
  const visH = Math.max(14, visBottom - visTop);

  const rx = clamp(Number.isFinite(anchor?.rx) ? anchor.rx : 0.38, 0.08, 0.92);
  const ry = clamp(Number.isFinite(anchor?.ry) ? anchor.ry : 0.52, 0.1, 0.9);

  const rawTipX = visLeft + visW * rx;
  const rawTipY = visTop + visH * ry;
  const tipX = Math.round(clamp(rawTipX, 14, vw - 38));
  const tipY = Math.round(clamp(rawTipY, safeTop - 2, vh - 40));
  const cx = Math.round(tipX - CURSOR_TIP_OFFSET_X);
  const cy = Math.round(tipY - CURSOR_TIP_OFFSET_Y);

  // Padded cursor bounding box so the speech bubble never sits on top of the pointer graphic.
  const curX1 = cx - 4;
  const curY1 = cy - 4;
  const curX2 = cx + 38;
  const curY2 = cy + 42;

  const compactW = visW <= 580;
  const compactH = visH <= 140;
  const gap = 14;

  const rawCandidates = [
    {
      side: 'right',
      rawBx: compactW ? Math.max(visRight + gap, curX2 + 10) : curX2 + 12,
      rawBy: tipY - 26
    },
    {
      side: 'below',
      rawBx: tipX - 34,
      rawBy: compactH ? Math.max(visBottom + gap, curY2 + 8) : curY2 + 10
    },
    {
      side: 'left',
      rawBx: (compactW ? Math.min(visLeft - gap, curX1 - 10) : curX1 - 12) - bw,
      rawBy: tipY - 26
    },
    {
      side: 'above',
      rawBx: tipX - 34,
      rawBy: (compactH ? Math.min(visTop - gap, curY1 - 10) : curY1 - 12) - bh
    }
  ];

  let best = null;
  for (const cand of rawCandidates) {
    const bx = clamp(Math.round(cand.rawBx), minBx, maxBx);
    const by = clamp(Math.round(cand.rawBy), minBy, maxBy);
    const curOverlap = overlapArea(bx, by, bx + bw, by + bh, curX1, curY1, curX2, curY2);
    const tgtOverlap = overlapArea(bx, by, bx + bw, by + bh, visLeft, visTop, visRight, visBottom);
    const clampDelta = Math.abs(bx - cand.rawBx) + Math.abs(by - cand.rawBy);
    const dist = pointToBoxDistance(tipX, tipY, bx, by, bx + bw, by + bh);
    const prefBonus = cand.side === preferSide ? -220 : 0;
    const score = curOverlap * 60 + tgtOverlap * 1.15 + clampDelta * 3.5 + dist * 1.4 + prefBonus;

    if (!best || score < best.score) {
      best = { side: cand.side, bx, by, score, curOverlap };
    }
  }

  let { side, bx, by } = best;

  // Final non-overlap guard on cramped viewports: if clamped bubble still grazes cursorBox, step clear where room exists.
  if (overlapArea(bx, by, bx + bw, by + bh, curX1, curY1, curX2, curY2) > 0) {
    if (curY2 + 8 + bh <= vh - margin) {
      by = clamp(Math.round(curY2 + 8), minBy, maxBy);
      side = 'below';
    } else if (curY1 - 8 - bh >= minBy) {
      by = clamp(Math.round(curY1 - 8 - bh), minBy, maxBy);
      side = 'above';
    } else if (curX2 + 8 + bw <= vw - margin) {
      bx = clamp(Math.round(curX2 + 8), minBx, maxBx);
      side = 'right';
    } else if (curX1 - 8 - bw >= minBx) {
      bx = clamp(Math.round(curX1 - 8 - bw), minBx, maxBx);
      side = 'left';
    }
  }

  let tx = 24;
  let ty = -6;
  if (side === 'below') {
    tx = clamp(Math.round(tipX - bx - 6), 18, Math.max(18, bw - 30));
    ty = -6;
  } else if (side === 'above') {
    tx = clamp(Math.round(tipX - bx - 6), 18, Math.max(18, bw - 30));
    ty = bh - 6;
  } else if (side === 'right') {
    tx = -6;
    ty = clamp(Math.round(tipY - by - 6), 18, Math.max(18, bh - 30));
  } else if (side === 'left') {
    tx = bw - 6;
    ty = clamp(Math.round(tipY - by - 6), 18, Math.max(18, bh - 30));
  }

  return {
    hasTarget: true,
    side,
    bx,
    by,
    bw,
    bh,
    tipX,
    tipY,
    cx,
    cy,
    tx,
    ty
  };
}

/**
 * Builds Scene-current curved cursor keyframes (`transform` ONLY — zero layout properties).
 * Adds a gentle perpendicular water-current bow and subtle pointer banking that settles cleanly at 0deg.
 */
export function buildCursorGlideKeyframes(from, to) {
  const dx = (to?.x ?? 0) - (from?.x ?? 0);
  const dy = (to?.y ?? 0) - (from?.y ?? 0);
  const dist = Math.hypot(dx, dy);
  if (dist < 2) {
    const t = `translate3d(${Math.round(to.x)}px, ${Math.round(to.y)}px, 0) rotate(0deg)`;
    return [{ transform: t }, { transform: t }];
  }
  // Gentle perpendicular arc capped at 26px so long glides feel organic like Scene F's water current.
  const arcMag = Math.min(26, dist * 0.11);
  const nx = -dy / dist;
  const ny = dx / dist;
  const sign = dx >= 0 ? -1 : 1;
  const midX = from.x + dx * 0.52 + nx * arcMag * sign;
  const midY = from.y + dy * 0.52 + ny * arcMag * sign;
  const nearX = to.x - dx * 0.025;
  const nearY = to.y - dy * 0.025;
  const bankDeg = clamp(dx * 0.018, -7, 7);

  return [
    { transform: `translate3d(${from.x.toFixed(1)}px, ${from.y.toFixed(1)}px, 0) rotate(0deg)`, offset: 0 },
    { transform: `translate3d(${midX.toFixed(1)}px, ${midY.toFixed(1)}px, 0) rotate(${bankDeg.toFixed(2)}deg)`, offset: 0.48 },
    { transform: `translate3d(${nearX.toFixed(1)}px, ${nearY.toFixed(1)}px, 0) rotate(${(-bankDeg * 0.25).toFixed(2)}deg)`, offset: 0.86 },
    { transform: `translate3d(${Math.round(to.x)}px, ${Math.round(to.y)}px, 0) rotate(0deg)`, offset: 1 }
  ];
}

function buildBubbleGlideKeyframes(from, to) {
  const dx = (to?.x ?? 0) - (from?.x ?? 0);
  const dy = (to?.y ?? 0) - (from?.y ?? 0);
  if (Math.hypot(dx, dy) < 2) {
    const t = `translate3d(${Math.round(to.x)}px, ${Math.round(to.y)}px, 0)`;
    return [{ transform: t }, { transform: t }];
  }
  const overX = to.x + clamp(dx * 0.02, -4, 4);
  const overY = to.y + clamp(dy * 0.02, -3, 3);
  return [
    { transform: `translate3d(${from.x.toFixed(1)}px, ${from.y.toFixed(1)}px, 0)`, offset: 0 },
    { transform: `translate3d(${overX.toFixed(1)}px, ${overY.toFixed(1)}px, 0)`, offset: 0.82 },
    { transform: `translate3d(${Math.round(to.x)}px, ${Math.round(to.y)}px, 0)`, offset: 1 }
  ];
}

export const CREATOR_GUIDE_URLS = {
  en: 'https://github.com/xMartin14091x/CWYLS-ModLoader/blob/main/docs/guide/pack-maker.md',
  th: 'https://github.com/xMartin14091x/CWYLS-ModLoader/blob/main/docs/guide/pack-maker.th.md'
};

/** Renders the per-page "?" Help popover panel (`data-motion="pop"`). */
export function renderHelpPopover(s, T, lang = null) {
  const page = helpPage(s);
  const tab = s.helpTab === 'glossary' ? 'glossary' : 'guide';
  const cfg = PAGE_HELP[page] || PAGE_HELP.studio;
  const resolvedLang = (lang || (T('tab_studio') === 'สตูดิโอ' ? 'th' : 'en')) === 'th' ? 'th' : 'en';
  const guideUrl = CREATOR_GUIDE_URLS[resolvedLang];
  const stepsHtml = cfg.steps.map((k) => `<li>${esc(T(k))}</li>`).join('');
  const keysHtml = cfg.keys
    .map(([kbd, labelKey]) => {
      const kbdHtml = kbd.includes(' / ')
        ? `<span>${kbd.split(' / ').map((x) => `<kbd>${esc(x)}</kbd>`).join(' / ')}</span>`
        : `<kbd>${esc(kbd)}</kbd>`;
      return `<div class="k-row">${kbdHtml}<span>${esc(T(labelKey))}</span></div>`;
    })
    .join('');

  const pageTitle = T(page === 'newpack' ? 'createPack' : `tab_${page}`);
  const terms = (PAGE_TERMS[page] || PAGE_TERMS.studio).map(id => `<div class="help-term"><dt>${esc(T(`g_${id}_t`))}</dt><dd>${esc(T(`g_${id}_d`))}</dd></div>`).join('');
  return `<div class="menu keys-menu help-menu" role="dialog" data-page="${page}" data-motion="pop" aria-label="${esc(T('helpTitle'))}">` +
    `<div class="help-head"><b>${esc(pageTitle)}</b><button type="button" data-act="help-close" aria-label="${esc(T('helpClose'))}">×</button></div>` +
    `<div class="help-tabs" role="tablist" aria-label="${esc(T('helpTitle'))}">` +
    ['guide', 'glossary'].map(id => `<button type="button" role="tab" id="help-${id}-tab" aria-controls="help-body" aria-selected="${tab === id}" tabindex="${tab === id ? 0 : -1}" data-act="help-tab" data-id="${id}" data-help-tab="${id}">${esc(T(id === 'guide' ? 'helpGuideTab' : 'helpGlossaryTab'))}</button>`).join('') +
    `</div><div class="help-content" id="help-body" role="tabpanel" tabindex="0" aria-labelledby="help-${tab}-tab">` +
    (tab === 'glossary' ? `<p class="help-connect">${esc(T('helpTermsIntro'))}</p><dl class="help-glossary">${terms}</dl>` : `<div class="help-sec">` +
    `<div class="menu-k">${esc(T('helpHowTitle'))} · ${esc(pageTitle)}</div>` +
    `<ol class="help-steps">${stepsHtml}</ol>` +
    `</div>` +
    `<div class="help-sec">` +
    `<div class="menu-k">${esc(T('helpConnectTitle'))}</div>` +
    `<p class="help-connect">${esc(T(cfg.connect))}</p>` +
    `</div>` +
    `<div class="help-sec">` +
    `<div class="menu-k">${esc(T('keysTitle'))}</div>` +
    `<div class="keys-list">${keysHtml}</div>` +
    `</div>`) + `</div><div class="help-foot">` +
    `<a class="btn ghost sm help-guide-link" href="${esc(guideUrl)}" target="_blank" rel="noopener noreferrer"><i class="ico ico-xs">${I.info}</i><span>${esc(T('helpGuideLink'))}</span></a>` +
    `<button type="button" class="btn peach sm help-tour-btn" data-act="tour-start"><i class="ico ico-xs">${I.spark}</i><span>${esc(T('tourStartBtn'))}</span></button>` +
    `</div>` +
    `</div>`;
}

const CURSOR_SVG =
  `<svg class="comp-cursor-svg" viewBox="0 0 36 40" width="36" height="40" fill="none" aria-hidden="true">` +
  `<defs>` +
  `<linearGradient id="comp-cur-fill" x1="4" y1="3" x2="26" y2="33" gradientUnits="userSpaceOnUse">` +
  `<stop offset="0%" stop-color="#FFF3DF"/>` +
  `<stop offset="48%" stop-color="#FFD787"/>` +
  `<stop offset="100%" stop-color="#FFAD87"/>` +
  `</linearGradient>` +
  `</defs>` +
  `<path d="M5.2 4.1L27.4 17.2C28.9 18.1 28.5 20.4 26.8 20.8L17.6 22.8L22.1 32.1C22.7 33.4 22.1 35 20.8 35.6L18.1 36.8C16.8 37.4 15.2 36.8 14.6 35.5L10.2 26.1L4.6 31.1C3.3 32.2 1.3 31.3 1.5 29.6L3.1 5.6C3.2 4.1 4.9 3.3 5.2 4.1Z" fill="url(#comp-cur-fill)" stroke="#12153A" stroke-width="2" stroke-linejoin="round"/>` +
  `<path d="M6.4 8.2L22.8 17.9L15.7 19.5L12.6 20.2L11.2 21.5L6.4 8.2Z" fill="rgba(255,255,255,0.34)"/>` +
  `<path d="M29.5 6.2L30.6 9.1L33.5 10.2L30.6 11.3L29.5 14.2L28.4 11.3L25.5 10.2L28.4 9.1L29.5 6.2Z" fill="#FFD787" stroke="#12153A" stroke-width="1.1" stroke-linejoin="round"/>` +
  `</svg>`;

export function renderCompanionBubbleInner(stepSpec, idx, total, T) {
  const isLast = idx >= total - 1;
  const dots = Array.from({ length: total }, (_, i) =>
    `<i class="comp-dot${i === idx ? ' on' : ''}" aria-hidden="true"></i>`
  ).join('');
  const whatTxt = stepSpec.whatKey ? T(stepSpec.whatKey) : '';

  return `<div class="comp-content" id="tour-content" data-step="${idx}" data-total="${total}" aria-live="polite">` +
    `<div class="tour-top">` +
    `<div class="comp-head-l">` +
    `<span class="comp-ava" aria-hidden="true"><i class="ico ico-xs">${I.spark}</i></span>` +
    `<span class="tour-step-badge">${esc(T('tourStepOf', { a: idx + 1, b: total }))}</span>` +
    `<span class="comp-dots" aria-hidden="true">${dots}</span>` +
    `</div>` +
    `<div class="comp-head-r">` +
    `<button type="button" class="lnk tour-skip" data-act="tour-skip">${esc(T('tourSkip'))}</button>` +
    `<kbd class="comp-esc" aria-hidden="true">Esc</kbd>` +
    `</div>` +
    `</div>` +
    `<div class="tour-title">${esc(T(stepSpec.titleKey))}</div>` +
    (whatTxt ? `<p class="tour-what">${esc(whatTxt)}</p>` : '') +
    `<div class="comp-do">` +
    `<span class="comp-do-k">${esc(T('tourDoLabel'))}</span>` +
    `<p class="tour-body">${esc(T(stepSpec.bodyKey))}</p>` +
    `</div>` +
    `<div class="tour-actions">` +
    `<button type="button" class="btn ghost sm" data-act="tour-prev"${idx === 0 ? ' disabled' : ''}>${esc(T('tourBack'))}</button>` +
    `<button type="button" class="btn peach sm" data-act="tour-next">${esc(T(isLast ? 'tourDone' : 'tourNext'))}</button>` +
    `</div>` +
    `</div>`;
}

/** One viewport-sized even-odd mask leaves the target clear without altering page stacking. */
export function renderTourOverlay(s, T) {
  const tour = s?.tour;
  if (!tour) return '';
  const { steps, index, stepSpec } = clampTourStep(tour.page || s.page || 'studio', tour.step || 0);
  if (!stepSpec) return '';

  return `<svg class="tour-spotlight" id="tour-spotlight" aria-hidden="true"><path id="tour-shade" fill-rule="evenodd"></path><rect id="tour-target" rx="6"></rect></svg>` +
    `<div class="comp-pos" id="tour-pos">` +
    `<div class="comp-bubble tour-card menu" id="tour-card" role="dialog" data-motion="pop" data-side="${esc(stepSpec.preferSide || 'below')}" aria-label="${esc(T(stepSpec.titleKey))}">` +
    `<span class="comp-tail" id="tour-tail" aria-hidden="true"></span>` +
    renderCompanionBubbleInner(stepSpec, index, steps.length, T) +
    `</div>` +
    `</div>` +
    `<div class="comp-cursor" id="tour-cursor" aria-hidden="true">` +
    `<span class="comp-halo"></span>` +
    `<span class="comp-ripple" id="tour-ripple"></span>` +
    `<span class="comp-cursor-inner">${CURSOR_SVG}</span>` +
    `</div>`;
}

/* ---------------- Live DOM Companion Controller (zero-jump tracking + Scene F glide) ---------------- */

let liveState = {
  mounted: false,
  page: null,
  step: -1,
  lang: null,
  lastStateRef: null,
  lastLayout: null,
  targetId: null,
  glideAnims: [],
  cleanupTracking: null,
  observedTarget: null,
  resizeObserver: null,
  mutationObserver: null
};

function cancelGlideAnims() {
  for (const a of liveState.glideAnims) {
    try { a?.cancel?.(); } catch { /* noop */ }
  }
  liveState.glideAnims = [];
}

function readMotionTimings(s, doc = globalThis.document) {
  const root = doc?.documentElement;
  const isShot = Boolean(root?.classList?.contains('shot'));
  const mode = root?.getAttribute?.('data-motion-mode') || s?.motion?.mode || 'full';
  if (isShot || mode === 'off') {
    return { mode: 'off', smallMs: 0, pageMs: 0, glideMs: 0 };
  }
  const fallback = effectiveDurations(s?.motion);
  let smallMs = fallback.smallMs;
  let pageMs = fallback.pageMs;
  try {
    const inlineSmall = parseFloat(root?.style?.getPropertyValue?.('--motion-small-ms') || '');
    const inlinePage = parseFloat(root?.style?.getPropertyValue?.('--motion-page-ms') || '');
    if (Number.isFinite(inlineSmall)) smallMs = inlineSmall;
    if (Number.isFinite(inlinePage)) pageMs = inlinePage;
    if (!Number.isFinite(inlineSmall) && typeof getComputedStyle === 'function' && root) {
      const cs = getComputedStyle(root);
      const csSmall = parseFloat(cs.getPropertyValue('--motion-small-ms') || '');
      const csPage = parseFloat(cs.getPropertyValue('--motion-page-ms') || '');
      if (Number.isFinite(csSmall)) smallMs = csSmall;
      if (Number.isFinite(csPage)) pageMs = csPage;
    }
  } catch {
    // keep fallback
  }
  if (mode === 'reduced') {
    return { mode: 'reduced', smallMs: Math.max(0, smallMs), pageMs: Math.max(0, pageMs), glideMs: 0 };
  }
  const glideMs = smallMs <= 0 && pageMs <= 0 ? 0 : Math.round(smallMs * 0.65 + pageMs * 0.38);
  return { mode: 'full', smallMs, pageMs, glideMs };
}

function findVisibleTarget(selectorList, doc) {
  if (!selectorList || !doc?.querySelectorAll) return null;
  for (const el of doc.querySelectorAll(selectorList)) {
    if (el.hidden || el.closest?.('[hidden]')) continue;
    // A virtualised list renders overscan rows outside its visible band: they are in the DOM but not on screen.
    const scroller = el.closest?.('#vb-list');
    if (scroller?.getBoundingClientRect && el.getBoundingClientRect) {
      const r = el.getBoundingClientRect(), band = scroller.getBoundingClientRect();
      if (r.bottom <= band.top || r.top >= band.bottom) continue;
    }
    if ((el.offsetWidth > 0 && el.offsetHeight > 0) || (el.getClientRects && el.getClientRects().length > 0)) {
      return el;
    }
  }
  return null;
}

export function resolveStepTarget(stepSpec, doc = globalThis.document) {
  if (!stepSpec || !doc) return null;
  return findVisibleTarget(stepSpec.selector, doc) || findVisibleTarget(stepSpec.fallback, doc);
}

/**
 * Computes the target's resting viewport box. During a Scene F `#main.is-streaming` page transition,
 * incoming zones carry temporary WAAPI translate3d/scale offsets; walking `offsetLeft`/`offsetTop`
 * minus scroll offsets yields the exact untransformed final box so the companion never jumps mid-stream.
 */
function measureRestingRect(el, doc = globalThis.document) {
  if (!el) return null;
  const mainEl = doc?.getElementById?.('main');
  const isStreaming = Boolean(
    mainEl?.classList?.contains('is-streaming') ||
    el.closest?.('.motion-stream-active')
  );
  if (isStreaming && el.offsetWidth > 0 && el.offsetHeight > 0) {
    let left = 0;
    let top = 0;
    let node = el;
    while (node) {
      left += node.offsetLeft || 0;
      top += node.offsetTop || 0;
      node = node.offsetParent;
    }
    let parent = el.parentElement;
    while (parent && parent !== doc.body && parent !== doc.documentElement) {
      left -= parent.scrollLeft || 0;
      top -= parent.scrollTop || 0;
      parent = parent.parentElement;
    }
    const win = doc.defaultView || globalThis;
    left -= win.scrollX || 0;
    top -= win.scrollY || 0;
    return { left, top, width: el.offsetWidth, height: el.offsetHeight };
  }
  const r = el.getBoundingClientRect();
  return { left: r.left, top: r.top, width: r.width, height: r.height };
}

function applyCompanionTransforms(posEl, cardEl, tailEl, cursorEl, layout) {
  posEl.style.transform = `translate3d(${layout.bx}px, ${layout.by}px, 0)`;
  cursorEl.style.transform = `translate3d(${layout.cx}px, ${layout.cy}px, 0)`;
  if (cardEl) cardEl.dataset.side = layout.side;
  if (tailEl) tailEl.style.transform = `translate3d(${layout.tx}px, ${layout.ty}px, 0) rotate(45deg)`;
  liveState.lastLayout = layout;
}

function pulseRipple(rippleEl, durationMs) {
  if (!rippleEl || typeof rippleEl.animate !== 'function' || durationMs <= 0) return null;
  return rippleEl.animate(
    [
      { transform: 'translate3d(-50%, -50%, 0) scale(0.35)', opacity: 0.9 },
      { transform: 'translate3d(-50%, -50%, 0) scale(1.55)', opacity: 0 }
    ],
    { duration: Math.max(260, durationMs), easing: 'cubic-bezier(0.22, 1, 0.36, 1)' }
  );
}

function ensureTrackingListeners(doc = globalThis.document) {
  if (liveState.cleanupTracking || !doc) return;
  const win = doc.defaultView || globalThis;
  const onViewportResize = () => {
    if (!liveState.mounted || !liveState.lastStateRef?.tour) return;
    positionTourCompanion(liveState.lastStateRef, { immediate: true, doc });
  };
  const onLayoutOrScroll = () => {
    if (!liveState.mounted || !liveState.lastStateRef?.tour) return;
    positionTourCompanion(liveState.lastStateRef, { immediate: false, doc });
  };

  win.addEventListener?.('resize', onViewportResize);
  win.visualViewport?.addEventListener?.('resize', onViewportResize);
  doc.addEventListener?.('scroll', onLayoutOrScroll, { capture: true, passive: true });

  const RO = win.ResizeObserver || globalThis.ResizeObserver;
  if (typeof RO === 'function') {
    const ro = new RO(() => onLayoutOrScroll());
    if (doc.documentElement) ro.observe(doc.documentElement);
    const mainEl = doc.getElementById('main');
    if (mainEl) ro.observe(mainEl);
    liveState.resizeObserver = ro;
  }

  const MO = win.MutationObserver || globalThis.MutationObserver;
  if (typeof MO === 'function') {
    const mainEl = doc.getElementById('main');
    if (mainEl) {
      const mo = new MO(() => {
        if (!mainEl.classList.contains('is-streaming') && liveState.mounted && liveState.lastStateRef?.tour) {
          positionTourCompanion(liveState.lastStateRef, { immediate: false, doc });
        }
      });
      mo.observe(mainEl, { attributes: true, attributeFilter: ['class'] });
      liveState.mutationObserver = mo;
    }
  }

  liveState.cleanupTracking = () => {
    win.removeEventListener?.('resize', onViewportResize);
    win.visualViewport?.removeEventListener?.('resize', onViewportResize);
    doc.removeEventListener?.('scroll', onLayoutOrScroll, { capture: true });
    try { liveState.resizeObserver?.disconnect(); } catch { /* noop */ }
    try { liveState.mutationObserver?.disconnect(); } catch { /* noop */ }
    liveState.resizeObserver = null;
    liveState.mutationObserver = null;
    liveState.observedTarget = null;
    liveState.cleanupTracking = null;
  };
}

function observeTargetElement(targetEl) {
  if (!liveState.resizeObserver || targetEl === liveState.observedTarget) return;
  if (liveState.observedTarget) {
    try { liveState.resizeObserver.unobserve(liveState.observedTarget); } catch { /* noop */ }
  }
  liveState.observedTarget = targetEl || null;
  if (targetEl) {
    try { liveState.resizeObserver.observe(targetEl); } catch { /* noop */ }
  }
}

function focusSafe(el) {
  if (!el || typeof el.focus !== 'function') return;
  try { el.focus({ preventScroll: true }); } catch { el.focus(); }
}

export function teardownTourCompanion(doc = globalThis.document, { restoreFocus = false } = {}) {
  const layer = doc?.getElementById?.('tour-layer');
  const ae = doc?.activeElement;
  const hadFocusInTour = Boolean(
    layer && ae && (layer.contains ? layer.contains(ae) : ae.closest?.('#tour-layer'))
  );
  cancelGlideAnims();
  liveState.cleanupTracking?.();
  liveState.mounted = false;
  liveState.page = null;
  liveState.step = -1;
  liveState.lang = null;
  liveState.lastStateRef = null;
  liveState.lastLayout = null;
  liveState.targetId = null;
  if (layer) {
    layer.hidden = true;
    layer.innerHTML = '';
  }
  if (restoreFocus || hadFocusInTour) {
    focusSafe(doc?.querySelector?.('[data-act="keys-menu"]'));
  }
}

/** Updates companion transforms for the current step without re-mounting DOM. */
export function positionTourCompanion(s, { immediate = false, doc = globalThis.document } = {}) {
  if (!doc) return null;
  const tour = s?.tour;
  const posEl = doc.getElementById('tour-pos');
  const cardEl = doc.getElementById('tour-card');
  const tailEl = doc.getElementById('tour-tail');
  const cursorEl = doc.getElementById('tour-cursor');
  if (!tour || !posEl || !cardEl || !cursorEl) return null;

  liveState.lastStateRef = s;
  const { stepSpec } = clampTourStep(tour.page || s.page || 'studio', tour.step || 0);
  if (!stepSpec) return null;

  const targetEl = resolveStepTarget(stepSpec, doc);
  if (targetEl && liveState.targetId !== stepSpec.id) {
    targetEl.scrollIntoView?.({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
    liveState.targetId = stepSpec.id;
  }
  observeTargetElement(targetEl);
  const targetRect = measureRestingRect(targetEl, doc);

  const win = doc.defaultView || globalThis;
  const vw = win.innerWidth || 1180;
  const vh = win.innerHeight || 700;
  const bw = cardEl.offsetWidth || Math.round(cardEl.getBoundingClientRect().width) || 328;
  const bh = cardEl.offsetHeight || Math.round(cardEl.getBoundingClientRect().height) || 178;

  const shade = doc.getElementById('tour-shade');
  const ring = doc.getElementById('tour-target');
  const spotlight = doc.getElementById('tour-spotlight');
  if (shade && spotlight) {
    const r = targetRect;
    let x = Math.max(4, r ? r.left - 6 : vw / 2), y = Math.max(4, r ? r.top - 6 : vh / 2);
    let right = Math.min(vw - 4, r ? r.left + r.width + 6 : x), bottom = Math.min(vh - 4, r ? r.top + r.height + 6 : y);
    // Clip the hole to any scrolling ancestor so overscan and offscreen content stay dim.
    for (let p = targetEl?.parentElement; p && p !== doc.body; p = p.parentElement) {
      const style = win.getComputedStyle(p);
      if (/(auto|scroll|hidden|clip)/.test(style.overflow + style.overflowX + style.overflowY)) {
        const clip = p.getBoundingClientRect();
        x = Math.max(x, clip.left); y = Math.max(y, clip.top);
        right = Math.min(right, clip.right); bottom = Math.min(bottom, clip.bottom);
      }
    }
    right = Math.max(x, right); bottom = Math.max(y, bottom);
    shade.setAttribute('d', `M0 0H${vw}V${vh}H0Z M${x} ${y}H${right}V${bottom}H${x}Z`);
    spotlight.dataset.target = targetEl ? stepSpec.id : '';
    for (const [key, val] of Object.entries({ x, y, width: right - x, height: bottom - y })) ring?.setAttribute(key, val);
  }

  const layout = computeCompanionLayout({
    targetRect,
    bubbleSize: { width: bw, height: bh },
    viewport: { width: vw, height: vh },
    preferSide: stepSpec.preferSide || 'auto',
    anchor: stepSpec.anchor || null
  });

  if (immediate) {
    cancelGlideAnims();
    applyCompanionTransforms(posEl, cardEl, tailEl, cursorEl, layout);
    return layout;
  }

  const prev = liveState.lastLayout;
  const moved = !prev ||
    Math.hypot(layout.cx - prev.cx, layout.cy - prev.cy) > 1 ||
    Math.hypot(layout.bx - prev.bx, layout.by - prev.by) > 1 ||
    layout.side !== prev.side;

  if (moved) {
    cancelGlideAnims();
    applyCompanionTransforms(posEl, cardEl, tailEl, cursorEl, layout);
  }
  return layout;
}

/**
 * Synchronises `#tour-layer` with `s.tour`:
 * - Mounts once when a guide opens, glides cursor + bubble across steps without DOM teardown,
 *   updates copy in place on language switch, and tracks target moves / window resizes with 0px jump.
 */
export function syncTourCompanion(s, T, { lang = 'en', doc = globalThis.document } = {}) {
  if (!doc) return;
  const layer = doc.getElementById('tour-layer');
  if (!layer) return;

  if (!s?.tour) {
    if (liveState.mounted || !layer.hidden) teardownTourCompanion(doc);
    return;
  }

  liveState.lastStateRef = s;
  const page = s.tour.page || s.page || 'studio';
  const { steps, index, stepSpec } = clampTourStep(page, s.tour.step || 0);
  if (!stepSpec) {
    teardownTourCompanion(doc);
    return;
  }

  const timings = readMotionTimings(s, doc);
  let posEl = doc.getElementById('tour-pos');
  let cardEl = doc.getElementById('tour-card');
  let tailEl = doc.getElementById('tour-tail');
  let cursorEl = doc.getElementById('tour-cursor');
  let rippleEl = doc.getElementById('tour-ripple');

  const isInitialMount = !liveState.mounted || layer.hidden || !posEl || !cardEl || !cursorEl;

  if (isInitialMount) {
    cancelGlideAnims();
    layer.hidden = false;
    layer.innerHTML = renderTourOverlay(s, T);
    posEl = doc.getElementById('tour-pos');
    cardEl = doc.getElementById('tour-card');
    tailEl = doc.getElementById('tour-tail');
    cursorEl = doc.getElementById('tour-cursor');
    rippleEl = doc.getElementById('tour-ripple');

    liveState.mounted = true;
    liveState.page = page;
    liveState.step = index;
    liveState.lang = lang;
    ensureTrackingListeners(doc);

    const layout = positionTourCompanion(s, { immediate: true, doc });
    openPop(cardEl);
    if (!doc.activeElement?.matches?.('input, textarea, select')) {
      focusSafe(cardEl?.querySelector?.('[data-act="tour-next"]:not([disabled])') || cardEl);
    }

    if (layout && typeof cursorEl?.animate === 'function') {
      if (timings.mode === 'full' && timings.glideMs > 0) {
        const enterDur = Math.min(400, Math.max(200, timings.glideMs));
        const from = { x: clamp(layout.cx - 34, 8, 2400), y: clamp(layout.cy - 22, 24, 1600) };
        const curAnim = cursorEl.animate(
          [
            { transform: `translate3d(${from.x}px, ${from.y}px, 0) rotate(-6deg)`, opacity: 0 },
            { transform: `translate3d(${layout.cx}px, ${layout.cy}px, 0) rotate(0deg)`, opacity: 1 }
          ],
          { duration: enterDur, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' }
        );
        const ripAnim = pulseRipple(rippleEl, enterDur);
        liveState.glideAnims.push(curAnim);
        if (ripAnim) liveState.glideAnims.push(ripAnim);
      } else if (timings.mode === 'reduced' && timings.smallMs > 0) {
        const fadeAnim = cursorEl.animate(
          [{ opacity: 0 }, { opacity: 1 }],
          { duration: timings.smallMs, easing: 'ease-out' }
        );
        liveState.glideAnims.push(fadeAnim);
      }
    }
    return;
  }

  const stepChanged = liveState.page !== page || liveState.step !== index;
  const langChanged = liveState.lang !== lang;

  if (!stepChanged && !langChanged) {
    positionTourCompanion(s, { immediate: false, doc });
    return;
  }

  // Read exact live visual coordinates BEFORE canceling any in-flight glide so rapid Next/Back clicks never jump.
  const prevCurRect = cursorEl.getBoundingClientRect();
  const prevPosRect = posEl.getBoundingClientRect();
  const fromCursor = { x: prevCurRect.left, y: prevCurRect.top };
  const fromBubble = { x: prevPosRect.left, y: prevPosRect.top };

  cancelGlideAnims();

  // Update bubble text & controls in place while preserving `.comp-tail` and `#tour-card`.
  const prevActive = doc.activeElement;
  const wasInCard = Boolean(
    prevActive && (cardEl.contains ? cardEl.contains(prevActive) : prevActive.closest?.('#tour-card'))
  );
  const prevAct = wasInCard ? prevActive.dataset?.act || null : null;

  cardEl.setAttribute('aria-label', T(stepSpec.titleKey));
  const oldContent = doc.getElementById('tour-content');
  if (oldContent) {
    oldContent.outerHTML = renderCompanionBubbleInner(stepSpec, index, steps.length, T);
  }

  liveState.page = page;
  liveState.step = index;
  liveState.lang = lang;

  const layout = positionTourCompanion(s, { immediate: true, doc });
  if (wasInCard) {
    const nextBtn =
      (prevAct && cardEl.querySelector?.(`[data-act="${prevAct}"]:not([disabled])`)) ||
      cardEl.querySelector?.('[data-act="tour-next"]:not([disabled])') ||
      cardEl;
    focusSafe(nextBtn);
  }
  if (!layout || !stepChanged) return;

  const contentEl = doc.getElementById('tour-content');
  if (timings.mode === 'full' && timings.glideMs > 0 && typeof cursorEl.animate === 'function') {
    const curAnim = cursorEl.animate(
      buildCursorGlideKeyframes(fromCursor, { x: layout.cx, y: layout.cy }),
      { duration: timings.glideMs, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' }
    );
    const posAnim = posEl.animate(
      buildBubbleGlideKeyframes(fromBubble, { x: layout.bx, y: layout.by }),
      { duration: timings.glideMs, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' }
    );
    liveState.glideAnims.push(curAnim, posAnim);
    if (contentEl?.animate && timings.smallMs > 0) {
      liveState.glideAnims.push(
        contentEl.animate(
          [
            { opacity: 0.42, transform: 'translate3d(6px, 0, 0)' },
            { opacity: 1, transform: 'translate3d(0, 0, 0)' }
          ],
          { duration: timings.smallMs, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' }
        )
      );
    }
    const ripAnim = pulseRipple(rippleEl, timings.glideMs);
    if (ripAnim) liveState.glideAnims.push(ripAnim);
  } else if (timings.mode === 'reduced' && timings.smallMs > 0) {
    if (contentEl?.animate) {
      liveState.glideAnims.push(
        contentEl.animate(
          [{ opacity: 0.38 }, { opacity: 1 }],
          { duration: timings.smallMs, easing: 'ease-out' }
        )
      );
    }
    if (cursorEl.animate) {
      liveState.glideAnims.push(
        cursorEl.animate(
          [{ opacity: 0.45 }, { opacity: 1 }],
          { duration: timings.smallMs, easing: 'ease-out' }
        )
      );
    }
  }
}

// Backwards-compatible alias for any external caller expecting positionTourSpotlight.
export const positionTourSpotlight = positionTourCompanion;
