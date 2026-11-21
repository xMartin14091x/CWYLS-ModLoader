// Zone 2: scenes, search, filter chips and the virtual line list. Pure render functions: (state, ctx) -> HTML string.
// ctx = {T, lang, strings}. The list renders only the rows in view (visibleWindow), so 2,169 lines cost ~25 nodes.
import { esc, rem } from '../util.js';
import { I } from './icons.js';
import { FILTERS, VOICE_FILTERS, subDot, dubDot, visibleWindow } from '../logic/progress.js';
import { groupName, visibleLines, rowsOf, warningsOf, groupOf, displayGroups, displayGroupOf } from '../selectors.js';
import { helpButton } from './help-popover.js';
import { warningHelp } from '../logic/warning-help.js';
import { renderPacksPage, renderExportPage, renderSettingsPage } from './packs-modal.js';
import { renderDropdown } from './dropdown.js';

export const ROW_REM = 2.55;
export const VB_ROW_PX = 58;
const VB_GROUP_PX = 38;
const VB_OVERSCAN_PX = VB_ROW_PX * 5;
const VISIBLE_SCENES = 6;

export function currentSceneId(s) {
  const raw = s.view?.group || s.line?.group || (s.lineId && s.byId?.get ? s.byId.get(s.lineId)?.group : null) || s.groups?.[0]?.id || s.lines?.[0]?.group || null;
  if (!raw) return null;
  return displayGroupOf(s, raw)?.id || raw;
}

export function sceneLinesOf(s) {
  const gid = currentSceneId(s);
  if (!gid || !s.lines?.length) return s.lines || [];
  const dg = displayGroupOf(s, gid);
  const filtered = s.lines.filter((l) => (dg ? dg.rawSet.has(l.group) : l.group === gid));
  return filtered.length ? filtered : s.lines;
}

export function renderSceneItems(s, ctx) {
  const { T, strings } = ctx;
  const dGroups = displayGroups(s);
  const shown = dGroups.slice(0, VISIBLE_SCENES);
  const curGroup = s.view.group ? (displayGroupOf(s, s.view.group)?.id || s.view.group) : null;
  const picked = curGroup && !shown.some((g) => g.id === curGroup) ? displayGroupOf(s, curGroup) : null;
  if (picked) shown.push(picked);
  let h = '';
  for (const g of shown) {
    const p = g.progress, total = p.totalLines || 1;
    h += `<li><button class="scn${curGroup === g.id ? ' on' : ''}" data-act="scene" data-id="${esc(g.id)}">` +
      `<span class="scn-n">${esc(groupName(s, g, T, strings))}</span>` +
      `<span class="scn-bars" aria-hidden="true"><i class="b-sub"><i style="width:${(100 * p.subtitlePresent / total).toFixed(1)}%"></i></i>` +
      `<i class="b-dub"><i style="width:${(100 * p.dubPresent / (p.dubbableLines || 1)).toFixed(1)}%"></i></i></span>` +
      `<span class="scn-c" title="${esc(`${T('subs')} ${p.subtitlePresent}/${p.totalLines} · ${T('dubs')} ${p.dubPresent}/${p.dubbableLines}`)}">${p.subtitlePresent}<span>/${p.totalLines}</span></span></button></li>`;
  }
  const more = dGroups.length - shown.length;
  if (more > 0) h += `<li class="scn-more">${esc(T('moreScenes', { n: more }))}</li>`;
  return h;
}

export function renderLeft(s, ctx) {
  const { T, strings } = ctx;
  const dGroups = displayGroups(s);
  const gid = currentSceneId(s);
  const gObj = gid ? (displayGroupOf(s, gid) || groupOf(s, gid)) : null;
  const gIdx = gid && dGroups.length ? Math.max(0, dGroups.findIndex((g) => g.id === gid)) : 0;
  const gTotal = Math.max(1, dGroups.length || 1);
  const title = gObj ? groupName(s, gObj, T, strings) : (gid || T('scenes'));
  const sLines = sceneLinesOf(s);
  let h = `<div class="scn-nav">` +
    `<button type="button" class="scn-arr" data-act="prev-scene" title="${esc(T('prevScene'))}" aria-label="${esc(T('prevScene'))}">◀</button>` +
    `<div class="scn-cur"><span class="scn-title">${esc(title)}</span><span class="scn-sub">${gIdx + 1}/${gTotal} · ${sLines.length}</span></div>` +
    `<button type="button" class="scn-arr" data-act="next-scene" title="${esc(T('nextScene'))}" aria-label="${esc(T('nextScene'))}">▶</button>` +
    `</div>`;
  h += '<div class="vlist" id="vlist" data-scroll="1"><div class="vl-inner" id="vl-inner"></div></div>';
  return h;
}

/** The rows inside #vl-inner for the current scroll position; also sets the inner height. Returns the HTML. */
export function renderRows(s, ctx, vlist) {
  const { T } = ctx;
  if (s.linesError) {
    // never a blank list: why, and a way to try again (the page also keeps trying on its own)
    return { height: 1, html: `<div class="vl-empty vl-error" role="alert"><b>${esc(T('linesFail_t'))}</b> ${esc(T('linesFail'))} ` +
      `<span class="vl-code">(${esc(s.linesError.code || 'unavailable')})</span> <button class="lnk" data-act="retry-lines">${esc(T('linesRetry'))}</button></div>` };
  }
  if (s.loadingLines) {
    return { height: 1, html: `<div class="vl-empty" role="status">${esc(T('loading'))}` +
      (s.linesAttempt > 0 ? `<br><span class="vl-slow">${esc(T('loadingSlow', { n: s.linesAttempt + 1 }))}</span>` : '') + '</div>' };
  }
  const sLines = sceneLinesOf(s), rh = ROW_REM * rem();
  const inner = { height: Math.max(sLines.length * rh, 1) };
  if (!sLines.length) {
    return { height: 1, html: `<div class="vl-empty">${esc(T('noMatch'))}</div>` };
  }
  const { from, to } = visibleWindow(sLines.length, vlist ? vlist.scrollTop : 0, (vlist && vlist.clientHeight) || 500, rh);
  let h = '';
  for (let i = from; i < to; i++) {
    const l = sLines[i], y = (i * rh).toFixed(1);
    const ss = subDot(l), ds = dubDot(l), hasText = !!(l.text && l.text.trim());
    const prev = hasText ? l.text.replace(/\n/g, ' ') : l.en;
    let marks = '';
    if (l.starred) marks += `<i class="ico ico-xs m-star" title="${esc(T('f_star'))}">${I.starF}</i>`;
    if (l.status === 'needs-retake') marks += `<i class="ico ico-xs m-flag" title="${esc(T('f_retake'))}">${I.flag}</i>`;
    const warnings = warningsOf(l, s.packLang);
    h += `<div class="line-row${warnings.length ? ' has-warning' : ''}" style="top:${y}px;height:${rh.toFixed(1)}px"><button class="row${l.id === s.lineId ? ' on' : ''}" data-act="line" data-id="${esc(l.id)}" title="${esc(l.id)}">` +
      `<span class="dots"><i class="d d-sub ${ss}" title="${esc(`${T('legendSub')}: ${T(`st_${ss}`)}`)}"></i><i class="d d-dub ${ds}"></i></span>` +
      `<span class="row-t${hasText ? '' : ' src'}">${esc(prev)}</span>${marks ? `<span class="row-m">${marks}</span>` : ''}</button>` +
      (warnings.length ? helpButton(warningHelp(warnings, T), `${T('f_warn')}: ${l.id}`, { warning: true, className: 'row-warning' }) : '') + '</div>';
  }
  return { height: inner.height, html: h };
}

/** Scroll position that brings the selected line into view with a little context; null when it already is. */
export function scrollTargetFor(s, vlist) {
  const sLines = sceneLinesOf(s);
  const idx = sLines.findIndex((l) => l && l.id === s.lineId);
  if (idx < 0 || !vlist) return null;
  const rh = ROW_REM * rem(), y = idx * rh;
  if (y < vlist.scrollTop + rh * 0.5) return Math.max(0, y - rh * 1.5);
  if (y + rh > vlist.scrollTop + vlist.clientHeight - rh * 0.5) return y + rh * 2.5 - vlist.clientHeight;
  return null;
}

function renderVbRow(l, s, T, strings, index, total) {
  const ss = subDot(l), ds = dubDot(l), hasText = !!(l.text && l.text.trim());
  const stKey = l.status === 'needs-retake' ? 'retake' : `st_${l.status || 'todo'}`;
  const scnName = groupName(s, displayGroupOf(s, l.group) || groupOf(s, l.group), T, strings);
  const dubTxt = !l.voice ? T('vf_unvoiced') : (l.ownedDub || l.publishedTakeId) ? T('in_pack') : l.takeCount > 0 ? `${T('take')} (${l.takeCount})` : T('vb_unrecorded');
  return `<button type="button" role="option" class="vb-row${l.id === s.lineId ? ' on' : ''}" style="height:${VB_ROW_PX}px;min-height:${VB_ROW_PX}px;flex:0 0 ${VB_ROW_PX}px" data-act="open-line-studio" data-id="${esc(l.id)}" data-vb-index="${index}" aria-posinset="${index + 1}" aria-setsize="${total}">` +
    `<span class="vb-c vb-c-st"><span class="dots"><i class="d d-sub ${ss}"></i><i class="d d-dub ${ds}"></i></span><span class="vb-st vb-st-${esc(l.status || 'todo')}">${esc(T(stKey))}</span></span>` +
    `<span class="vb-c vb-c-id"><b>${esc(scnName)}</b><small class="mono">${esc(l.id)}${l.voice ? ` · ${esc(T('satone'))}` : ''}</small></span>` +
    `<span class="vb-c vb-c-orig"><span class="vb-ja" lang="ja">${esc(l.ja)}</span><span class="vb-en" lang="en">${esc(l.en)}</span></span>` +
    `<span class="vb-c vb-c-sub${hasText ? '' : ' empty'}">${esc(hasText ? l.text.replace(/\n/g, ' ') : '—')}</span>` +
    `<span class="vb-c vb-c-dub ${ds}">${esc(dubTxt)}${l.durationSec ? ` <small class="mono">${l.durationSec.toFixed(1)}s</small>` : ''}</span>` +
    `</button>`;
}

const statusGroups = (T) => [
  ['todo', T('st_todo')],
  ['draft', T('st_draft')],
  ['needs-retake', T('retake')],
  ['done', T('st_done')]
];

/** Voice lines in their current visual order. Search and filters still apply to the entire catalogue. */
export function voiceLinesFor(s) {
  const lines = visibleLines(s);
  if (!s.view.groupByStatus) return lines;
  return ['todo', 'draft', 'needs-retake', 'done'].flatMap((status) => lines.filter((l) => (l.status || 'todo') === status));
}

function voiceEntries(s, T) {
  const lines = visibleLines(s);
  if (!s.view.groupByStatus) return lines.map((line, index) => ({ kind: 'row', line, index, height: VB_ROW_PX }));
  const entries = [], ordered = voiceLinesFor(s);
  let index = 0;
  for (const [status, title] of statusGroups(T)) {
    const count = lines.filter((l) => (l.status || 'todo') === status).length;
    if (!count) continue;
    entries.push({ kind: 'group', status, title, count, height: VB_GROUP_PX });
    for (let n = 0; n < count; n++, index++) entries.push({ kind: 'row', line: ordered[index], index, height: VB_ROW_PX });
  }
  return entries;
}

/** Pixel offset of a logical row in the virtual Voice lines list. */
export function voiceRowTop(s, ctx, rowIndex) {
  let top = 0;
  for (const entry of voiceEntries(s, ctx.T)) {
    if (entry.kind === 'row' && entry.index === rowIndex) return top;
    top += entry.height;
  }
  return 0;
}

/** Render only entries near the current Voice lines viewport, with spacers preserving full scroll reach. */
export function renderVoiceRows(s, ctx, list) {
  const { T, strings } = ctx;
  const lines = voiceLinesFor(s);
  if (!lines.length) return `<div class="vl-empty">${esc(T('noMatch'))} <button class="lnk" data-act="filter" data-id="all">${esc(T('clearFilter'))}</button></div>`;
  const entries = voiceEntries(s, T);
  const scrollTop = list?.scrollTop || 0;
  const viewportHeight = list?.clientHeight || 600;
  const start = Math.max(0, scrollTop - VB_OVERSCAN_PX);
  const end = scrollTop + viewportHeight + VB_OVERSCAN_PX;
  let total = 0, from = 0, to = entries.length;
  for (let i = 0; i < entries.length; i++) {
    const next = total + entries[i].height;
    if (next < start) from = i + 1;
    if (total <= end) to = i + 1;
    total = next;
  }
  let before = 0;
  for (let i = 0; i < from; i++) before += entries[i].height;
  let shown = 0, html = '';
  if (before) html += `<div aria-hidden="true" style="height:${before}px;min-height:${before}px;flex:0 0 ${before}px"></div>`;
  for (let i = from; i < to; i++) {
    const entry = entries[i];
    shown += entry.height;
    if (entry.kind === 'group') {
      html += `<div class="vb-group-h" style="height:${VB_GROUP_PX}px;min-height:${VB_GROUP_PX}px;flex:0 0 ${VB_GROUP_PX}px"><span class="vb-st vb-st-${entry.status}">${esc(entry.title)}</span><span class="vb-group-c">${entry.count}</span></div>`;
    } else {
      html += renderVbRow(entry.line, s, T, strings, entry.index, lines.length);
    }
  }
  const after = Math.max(0, total - before - shown);
  if (after) html += `<div aria-hidden="true" style="height:${after}px;min-height:${after}px;flex:0 0 ${after}px"></div>`;
  return html;
}

export function renderPageView(s, ctx) {
  const { T, strings } = ctx;
  const page = s.page || 'studio';
  if (page === 'packs') return renderPacksPage(s, ctx);
  if (page === 'export') return renderExportPage(s, ctx);
  if (page === 'settings') return renderSettingsPage(s, ctx);
  if (page !== 'lines') {
    const label = T(`tab_${page}`) || page;
    return `<div class="page-placeholder"><div class="pp-card"><h2>${esc(label)}</h2><p>${esc(T('pagePlaceholder', { page: label }))}</p></div></div>`;
  }
  const vf = s.view.voice || 'all', cf = s.view.char || 'all';
  const gf = s.view.group ? (displayGroupOf(s, s.view.group)?.id || s.view.group) : '';
  const dGroups = displayGroups(s);
  const byStatus = !!s.view.groupByStatus;
  const lines = visibleLines(s);
  const chipList = [
    ['all', T('f_all')],
    ['todo', T('f_todo')],
    ['draft', T('f_draft')],
    ['done', T('f_done')],
    ['retake', T('f_retake')],
    ['nosub', T('f_nosub')],
    ['nodub', T('f_nodub')],
    ['unrec', T('vb_unrecorded')],
    ['rec', T('vb_recorded')],
    ['warn', T('f_warn')],
    ['star', T('f_star')]
  ];
  const scnOpts = [
    { value: '', label: T('allScenes') },
    ...dGroups.map((g) => ({ value: g.id, label: `${groupName(s, g, T, strings)} (${g.progress?.totalLines || 0})` }))
  ];
  const charOpts = [
    { value: 'all', label: T('cf_all') },
    { value: 'satone', label: T('satone') },
    { value: 'none', label: T('cf_narrator') }
  ];
  let h = `<div class="vb-page">`;
  h += `<div class="vb-bar">` +
    `<div class="vb-bar-row">` +
    `<div class="seg vb-mode" role="group">` +
    `<button type="button" class="${!byStatus ? 'on' : ''}" data-act="vb-mode" data-id="all">${esc(T('vb_allRows'))}</button>` +
    `<button type="button" class="${byStatus ? 'on' : ''}" data-act="vb-mode" data-id="status">${esc(T('vb_byStatus'))}</button>` +
    `</div>` +
    renderDropdown({ id: 'dd-scnfilter', dataCh: 'scnfilter', value: gf, options: scnOpts, ariaLabel: T('scenes'), className: 'dd-scnfilter' }) +
    renderDropdown({ id: 'dd-charfilter', dataCh: 'charfilter', value: cf, options: charOpts, ariaLabel: T('cf_label'), className: 'dd-charfilter' }) +
    `<div class="vchips" role="group" aria-label="${esc(T('vf_label'))}">` +
    VOICE_FILTERS.map((v) => `<button type="button" class="vchip vchip-${v}${vf === v ? ' on' : ''}" data-act="vfilter" data-id="${v}">${esc(T(`vf_${v}`))}</button>`).join('') +
    `</div>` +
    `<label class="search vb-search"><i class="ico ico-s">${I.search}</i><input data-in="q" type="search" placeholder="${esc(T('search'))}" value="${esc(s.view.q)}"></label>` +
    `<button type="button" class="btn peach sm vb-rec-next" data-act="next-unrecorded" title="${esc(T('nextUnrecTip'))}"><i class="ico ico-xs">${I.nextI}</i><span>${esc(T('vb_recNext'))}</span></button>` +
    `</div>` +
    `<div class="vb-bar-row vb-chips-row">` +
    `<div class="chips" role="group">${chipList.map(([f, lbl]) => `<button type="button" class="chip chip-${f}${s.view.filter === f ? ' on' : ''}" data-act="filter" data-id="${f}">${esc(lbl)}</button>`).join('')}</div>` +
    `<span class="vb-count" id="l-count">${esc(T(lines.length === 1 ? 'linesCountOne' : 'linesCount', { n: lines.length }))}</span>` +
    `</div>` +
    `</div>`;
  h += `<div class="vb-table">` +
    `<div class="vb-thead">` +
    `<span>${esc(T('vb_colStatus'))}</span>` +
    `<span>${esc(T('vb_colLine'))}</span>` +
    `<span>${esc(T('vb_colOrig'))}</span>` +
    `<span>${esc(T('vb_colSub'))}</span>` +
    `<span>${esc(T('vb_colDub'))}</span>` +
    `</div>` +
    `<div class="vb-tbody" id="vb-list" data-scroll="1" tabindex="0" role="listbox" aria-label="${esc(T('tab_lines'))}">`;
  h += `</div></div></div>`;
  return h;
}
