// Zone 3: the line (a lit window). `renderCard` builds the line's shell once per line; `renderLive` returns the parts that
// follow typing (meters, QA, glossary, footer) so the textarea is never rebuilt while the creator types.
// ctx = {T, lang, strings, glossary}. All data goes through esc().
import { esc, sec } from '../util.js';
import { I } from './icons.js';
import { ROOM_WINDOW } from './art.js';
import { helpButton } from './help-popover.js';
import { cplCount, maxCpl, wrapLines } from '../logic/thai.js';
import { readingSpeed, readingClass } from '../logic/reading.js';
import { qaList } from '../logic/qa.js';
import { glossaryFor } from '../logic/glossary.js';
import { subDot } from '../logic/progress.js';
import { sourceNotes, packName, shortText } from '../logic/source.js';
import { isReadOnly, canEditText, featureOn, groupOf, displayGroupOf, groupName, editorText, basisFor, neighbours, positionInGroup, packOf } from '../selectors.js';

function adjRow(kind, l, T) {
  const label = T(kind === 'prev' ? 'before' : 'after');
  if (!l) return `<div class="adj adj-${kind} none"><span class="adj-k">${esc(label)}</span><span class="adj-t">${esc(T(kind === 'prev' ? 'sceneStart' : 'sceneEnd'))}</span></div>`;
  const t = l.text && l.text.trim() ? l.text.replace(/\n/g, ' ') : l.en;
  return `<button class="adj adj-${kind}" data-act="line" data-id="${esc(l.id)}"><span class="adj-k">${esc(label)}</span><span class="adj-t">${esc(t)}</span></button>`;
}

/** A key that changes when the card's shell must be rebuilt (different line, pack, language, read-only state). */
export const cardKey = (s, lang) => [s.line?.id, s.packId, lang, s.packLang, canEditText(s), featureOn(s, 'workflow'), !!packOf(s)?.restartRequired].join('|');

export function renderCard(s, ctx) {
  const { T, strings } = ctx;
  const l = s.line, ro = !canEditText(s);
  if (!l) return `<div class="vl-empty">${esc(T('loading'))}</div>`;
  const g = displayGroupOf(s, l.group) || groupOf(s, l.group), { pos, of } = positionInGroup(s, l);
  const mood = l.context?.mood && strings.moods[l.context.mood], pose = l.context?.pose && strings.poses[l.context.pose];
  const langName = T(`lang_${s.packLang}`);
  let h = `<div class="ctx"><span class="who"><i class="ava">${I.satone}</i><b>${esc(T('satone'))}</b></span>` +
    `<span class="ctx-scene">${esc(groupName(s, g, T, strings))} <span class="ctx-pos">${pos}/${of}</span></span>` +
    (mood ? `<span class="tag">${esc(mood)}</span>` : '') + (pose ? `<span class="tag">${esc(pose)}</span>` : '') +
    `<span class="sp"></span><span class="lid" title="line id">${esc(l.id)}</span><button class="star-btn" id="star-btn" data-act="star-line"${isReadOnly(s) || l.readOnly || !featureOn(s, 'workflow') ? ' disabled' : ''}></button></div>`;
  h += `<div class="refs"><div class="ref"><span class="ref-k">${esc(T('refJa'))}</span><span class="ref-t ja" lang="ja">${esc(l.ja)}</span></div>` +
    `<div class="ref"><span class="ref-k">${esc(T('refEn'))}</span><span class="ref-t en" lang="en">${esc(l.en)}</span></div>` +
    `<div class="ref ref-d"><span class="ref-k">${esc(T('origDur', { s: l.durationSec == null ? '—' : sec(l.durationSec) }))}</span>` +
    `<span class="odur"><i style="width:${Math.min(100, (l.durationSec || 0) / 6 * 100).toFixed(1)}%"></i></span></div></div>`;
  h += `<div class="ed-head"><span class="ed-k">${esc(T(ro ? 'packSub' : 'yourSub', { lang: ctx.lang === 'th' ? `ภาษา${langName}` : langName }))}</span>` +
    `<span class="ed-hint"><kbd>Shift</kbd>+<kbd>Enter</kbd> ${esc(T('shiftEnter').replace(/^Shift\+Enter\s*/, ''))}</span></div>`;
  h += `<div class="editor${ro ? ' is-ro' : ''}" id="editor">` +
    `<textarea id="ed" rows="2" aria-describedby="ed-limit" spellcheck="false" lang="${esc(s.packLang)}" placeholder="${esc(T(packOf(s)?.restartRequired ? 'b_first' : 'placeholder'))}"${ro ? ' readonly' : ''}>${esc(editorText(s))}</textarea>` +
    '<span class="ed-limit" id="ed-limit" role="status" aria-live="polite"></span><div class="first" id="first"></div></div>';
  // TD-3: the loader cannot keep Studio work for this line (its id cannot name a file); say so instead of failing on save.
  if (l.readOnly) h += `<div class="ro-line srcrow"><i class="ico ico-xs">${I.lock}</i><span>${esc(T('ro_line_id', { id: l.id }))}</span></div>`;
  h += '<div class="srcnote" id="srcnote"></div><div class="meters" id="meters"></div><div class="qa" id="qa"></div>';
  return h;
}

/** The "another pack wins" notes (subtitle and dub) as HTML; empty when this pack is what the game uses. Pack names are escaped. */
export function renderSourceNote(s, T) {
  const n = sourceNotes(s.line, s.packId);
  const rows = [];
  if (n.sub) rows.push(T(n.sub.kind === 'wins' ? 'src_sub_wins' : 'src_sub_only', { pack: packName(s.packs, n.sub.packId), text: shortText(n.sub.text) }));
  if (n.dub) rows.push(T(n.dub.kind === 'wins' ? 'src_dub_wins' : 'src_dub_only', { pack: packName(s.packs, n.dub.packId) }));
  return rows.map((r) => `<div class="srcrow"><i class="ico ico-xs">${I.info}</i><span>${esc(r)}</span></div>`).join('');
}

/** @returns {{star:{on:boolean,title:string}, first:string, src:string, meters:string, qa:string, foot:string}} */
export function renderLive(s, ctx, text) {
  const { T, lang, glossary } = ctx, l = s.line, ro = !canEditText(s), pl = s.packLang;
  const basis = basisFor(s, l), w = wrapLines(text, pl), max = maxCpl(pl);
  const cps = readingSpeed(text, pl, basis.seconds);
  const empty = !text.trim();
  // meters
  let m = `<div class="wrapv"><span class="mk">${esc(T('wrapLabel'))}</span>`;
  m += !w.length ? '<span class="wrapv-t dim">—</span>' : `<span class="wrapv-t">${w.map((x, i) => `<span class="wl${i > 1 ? ' over' : ''}">${esc(x)}</span>`).join('<i class="brk" aria-label="line break">↵</i>')}</span>`;
  m += '</div><div class="mrow"><div class="cpl">';
  (w.length ? w : ['']).forEach((x, i) => {
    const c = cplCount(x, pl);
    m += `<span class="cpl-i${c > max || i > 1 ? ' bad' : ''}"><span class="cpl-n">${i + 1}</span><i class="cbar"><i style="width:${Math.min(100, c / max * 100).toFixed(1)}%"></i></i><span class="cpl-v"><b>${c}</b>/${max}</span></span>`;
  });
  m += helpButton(pl === 'th' ? T('countNote') : T('countNoteEn'), T('countHelp')) + '</div>';
  m += `<button class="cps ${readingClass(cps, text)}" data-act="cps-src"${basis.canSwitch ? '' : ' disabled'} title="${esc(T('comfy'))}"><span class="mk">${esc(T('reading'))}</span>` +
    `<b>${empty ? '–' : cps.toFixed(1)}</b><span class="u">${esc(T('cps'))}</span><span class="cps-src">${esc(basis.source === 'dub' ? T('vsDub', { s: sec(basis.seconds) }) : T('vsOrig', { s: sec(basis.seconds) }))}` +
    `${basis.canSwitch ? ` <i class="ico ico-xs">${I.chev}</i>` : ''}</span></button></div>`;
  // QA and glossary
  const qa = qaList(l, text, { lang: pl, seconds: basis.seconds }), terms = glossaryFor(l, glossary, pl, text);
  let q = '<div class="qa-p">';
  if (!empty && !qa.length) q += `<span class="pill ok"><i class="ico ico-xs">${I.check}</i>${esc(T('qa_ok'))}</span>`;
  for (const x of qa) {
    const label = x.k === 'ph' ? T('qa_placeholder', { x: x.x }) : x.k === 'tag' ? T('qa_tag', { x: x.x }) : x.k === 'mai' ? T('qa_mai') : x.k === 'double' ? T('qa_double') : x.k === 'lines' ? T('qa_lines', { n: x.n }) : T('qa_fast', { v: x.v });
    const fix = x.k === 'ph' || x.k === 'tag' ? T('insert') : x.k === 'mai' || x.k === 'double' ? T('fix') : '';
    q += `<span class="pill warn"><i class="ico ico-xs">${I.warn}</i>${esc(label)}${fix && !ro ? `<button class="pill-fix" data-act="qa-fix" data-k="${x.k}" data-x="${esc(x.x || '')}">${esc(fix)}</button>` : ''}</span>`;
  }
  q += '</div>';
  if (terms.length) {
    q += `<div class="gl"><span class="mk">${esc(T('glossary'))}</span>${terms.map((x) => `<button class="gchip${x.used ? ' used' : ''}" data-act="gloss" data-t="${esc(x.target)}" title="${esc(T('glossUse', { t: x.target }) + (x.used ? '' : ` · ${T('glossMissing')}`))}"${ro ? ' disabled' : ''}>` +
      `<span class="g-src">${esc(x.source)}</span><span class="g-ar">→</span><span class="g-t">${esc(x.target)}</span>${x.used ? `<i class="ico ico-xs">${I.check}</i>` : ''}</button>`).join('')}</div>`;
  }
  // first-run ribbon removed in de-cramped single-card layout
  const first = '';
  // a stale save: the creator's draft is kept, the pack's version is offered, nothing is overwritten silently
  if (s.draft?.status === 'conflict') {
    const theirs = s.draft.theirs ? `<span class="cflt-v" title="${esc(s.draft.theirs.text)}">${esc(T('conflict_theirs'))} “${esc(s.draft.theirs.text.replace(/\n/g, ' '))}”</span>` : '';
    q = `<div class="cflt"><i class="ico ico-s">${I.warn}</i><span class="cflt-t"><b>${esc(T('conflict_t'))}</b>${theirs}</span>` +
      `<span class="cflt-a"><button class="btn ghost sm" data-act="conflict-mine">${esc(T('conflict_keep'))}</button>` +
      `<button class="btn peach sm" data-act="conflict-theirs"${s.draft.theirs ? '' : ' disabled'}>${esc(T('conflict_take'))}</button></span></div>`;
  }
  return { star: { on: l.starred, title: T(l.starred ? 'unstarLine' : 'starLine') }, first, src: renderSourceNote(s, T), meters: m, qa: q, foot: renderFoot(s, ctx, text) };
}

function ago(at, T) {
  const m = Math.round(Math.max(0, Date.now() - at) / 60000);
  if (m < 1) return T('histNow');
  if (m < 60) return T('histMin', { n: m });
  const hh = Math.round(m / 60);
  if (hh < 20) return T('histHour', { n: hh });
  const d = Math.round(hh / 24);
  return d <= 1 ? T('histDay') : T('histDays', { n: d });
}

function renderFoot(s, ctx, text) {
  const { T } = ctx, l = s.line, ro = !canEditText(s), ss = l.status === 'needs-retake' ? 'draft' : (l.status || subDot({ ...l, text })), noFlow = isReadOnly(s) || !featureOn(s, 'workflow');
  const hist = s.history?.items || [];
  let h = `<div class="seg" role="radiogroup">${['todo', 'draft', 'done'].map((x) => {
    const kbd = x === 'done' ? '<kbd class="seg-k">↵</kbd>' : '';
    const tip = x === 'done' ? ` title="${esc(T('doneKeyTip'))}"` : '';
    return `<button class="seg-b s-${x}${ss === x ? ' on' : ''}" data-act="status" data-id="${x}"${tip}${noFlow ? ' disabled' : ''}><i class="d d-sub ${x}"></i>${esc(T(`st_${x}`))}${kbd}</button>`;
  }).join('')}</div>`;
  h += `<div class="hist-w"><button class="btn ghost sm hist-b${s.menu === 'hist' ? ' on' : ''}" data-act="hist"${isReadOnly(s) || !featureOn(s, 'projects') ? ' disabled' : ''}><i class="ico ico-s">${I.clock}</i>${esc(T('history'))}` +
    `${hist.length > 1 ? ` <span class="cnt">${hist.length - 1}</span>` : ''}</button>`;
  if (s.menu === 'hist') {
    h += '<div class="menu hist-menu" data-motion="pop">';
    if (hist.length < 2) h += `<div class="hm-empty">${esc(T('histEmpty'))}</div>`;
    hist.forEach((v, i) => {
      const body = v.text === null ? T('histRemoved') : v.text.replace(/\n/g, ' ↵ ');
      h += `<div class="hm-i${i === 0 ? ' now' : ''}"><span class="hm-t">${esc(body)}</span><span class="hm-m">${esc(i === 0 ? T('histNow') : ago(v.at, T))}</span>` +
        `${i === 0 ? '' : `<button class="btn ghost xs" data-act="revert" data-id="${esc(v.id)}">${esc(T('revert'))}</button>`}</div>`;
    });
    if (s.history?.gap) h += `<div class="hm-empty">${esc(T('histGap'))}</div>`;
    h += '</div>';
  }
  h += '</div>';
  let note = '', cls = '', icon = `<i class="ico ico-xs">${I.check}</i>`;
  const ds = s.draft?.status;
  if (ds === 'conflict') { note = T('conflict_t'); cls = ' off'; icon = `<i class="ico ico-xs">${I.warn}</i>`; }
  else if (ro) { note = T(isReadOnly(s) ? 'roNote' : 'feat_subtitles'); cls = ' ro'; icon = `<i class="ico ico-xs">${I.lock}</i>`; }
  else if (!s.conn.connected) { note = T('offNote'); cls = ' off'; icon = `<i class="ico ico-xs">${I.plug}</i>`; }
  else if (ds === 'saving' || ds === 'typing') { note = T('saving'); cls = ' saving'; icon = '<i class="spin-dot"></i>'; }
  else if (ds === 'failed') { note = T('saveFailed'); cls = ' off'; icon = `<i class="ico ico-xs">${I.warn}</i>`; }
  else if (!ds && text !== (l.text || '')) note = ''; // typed, but saving is not wired yet (M3): claim nothing
  // "live in game" is claimed only after a save the loader answered with live:true; on a fresh load the line is just in the pack
  else if (s.saveLive === false) note = s.saveNote ? T('savedNotLive', { note: s.saveNote }) : T('savedPlain');
  else note = T(s.saveLive ? 'saved' : 'savedPlain');
  if (!ro && !text.trim() && !hist.length) note = '';
  if (note) h += `<span class="save${cls}">${icon}${esc(note)}</span>`;
  return h;
}
