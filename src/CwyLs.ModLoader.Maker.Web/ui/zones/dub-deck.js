// Zone 4: the dub deck, a small cassette deck on the horizon. Pure render: (state, ctx) -> HTML string.
// Waveforms are drawn only from the creator's own takes (`s.peaks[takeId]`, computed in the browser), never from game audio.
import { esc, sec, fmtTime } from '../util.js';
import { I } from './icons.js';
import { isReadOnly, currentTake, canDub, featureOn, hasPackVoiceDefault, takeOriginDraft, packOf } from '../selectors.js';
import { consentOk } from '../logic/takes.js';
import { renderDropdown } from './dropdown.js';
import { helpButton } from './help-popover.js';

const ORIGINS = [['own', 'o_own'], ['ai-assisted', 'o_assist'], ['ai-generated', 'o_gen']];

function reel() {
  return '<svg viewBox="0 0 40 40"><circle cx="20" cy="20" r="18" class="r-o"/><circle cx="20" cy="20" r="6.5" class="r-i"/>' +
    '<path class="r-s" d="M20 4.5v6M20 29.5v6M6.6 12.2l5.2 3M28.2 24.8l5.2 3M6.6 27.8l5.2-3M28.2 15.2l5.2-3"/></svg>';
}

export function waveSvg(peaks, normalise) {
  if (!peaks || !peaks.length) return '<svg class="wsvg" viewBox="0 0 96 100" preserveAspectRatio="none"></svg>';
  let norm = 1;
  if (normalise) { const mx = Math.max(...peaks); norm = mx > 0 ? 0.86 / mx : 1; }
  let bars = '';
  for (let i = 0; i < peaks.length; i++) {
    const v = Math.min(1, peaks[i] * norm), hgt = Math.max(1.5, v * 88);
    bars += `<rect x="${(i + 0.18).toFixed(2)}" y="${(50 - hgt / 2).toFixed(2)}" width="0.64" height="${hgt.toFixed(2)}" rx="0.3"/>`;
  }
  return `<svg class="wsvg" viewBox="0 0 ${peaks.length} 100" preserveAspectRatio="none">${bars}</svg>`;
}

export function renderFit(l, td, T) {
  const od = l?.durationSec || 0;
  const scale = Math.max(od, td || 0, 0.1) * 1.08;
  return `<div class="fit-bars"><i class="fb o" style="width:${(od / scale * 100).toFixed(1)}%"></i>${td != null ? `<i class="fb t" style="width:${(td / scale * 100).toFixed(1)}%"></i>` : ''}</div>` +
    `<div class="fit-t">${td != null ? `${esc(T('timing', { a: sec(td), b: sec(od) }))} · <b>${esc(td > od ? T('gameWaits') : T('shorter'))}</b>` : esc(T('origDur', { s: sec(od) }))}</div>`;
}

export function renderDeck(s, ctx) {
  const { T } = ctx, l = s.line;
  if (!l) return '';
  const ro = !canDub(s), pending = !!packOf(s)?.restartRequired, t = currentTake(s), takes = l.takes;
  const draft = t && s.trimDraft && s.trimDraft.takeId === t.id ? s.trimDraft : null;
  const trimS = t ? (draft ? draft.start : t.trimStartSec ?? 0) : 0, trimE = t ? (draft ? draft.end : t.trimEndSec ?? t.sourceDurationSec ?? 0) : 0;
  const published = l.publishedTakeId;
  const tIndex = t ? takes.findIndex((x) => x.id === t.id) : -1;
  const fav = l.favouriteTakeId ?? null;
  const rec = s.rec || 'idle';
  const dur = t ? Math.max(0, trimE - trimS) : null;
  let h = `<div class="deck-body${ro ? ' is-ro' : ''}${rec !== 'idle' ? ' is-rec' : ''}${s.playingTake ? ' is-play' : ''}${s.dragOver ? ' drag' : ''}" id="deck-body">`;
  h += `<div class="deck-top"><span class="stripes" aria-hidden="true"><i></i><i></i><i></i></span><span class="deck-name">${esc(T('deck'))}</span>` +
    `<span class="deck-file">${esc(T('fileNote'))}</span><span class="screws" aria-hidden="true"><i></i><i></i></span></div><div class="deck-grid">`;

  // mic + record
  h += '<div class="d-mic">';
  if (s.mic !== 'ready') {
    const msg = pending ? T('b_first') : !featureOn(s, 'dubs') ? T('feat_dubs') : s.mic === 'off' ? T('mic_off') : s.micError ? T(s.micError) : T('setupNote');
    h += `<div class="recwrap off"><button class="recbtn" data-act="record" title="${esc(T('t_needMic'))}" aria-disabled="true"><i class="ico">${I.mic}</i></button></div>` +
      `<div class="setup"><button class="btn peach" data-act="setup-mic"${ro || s.mic === 'asking' ? ' disabled' : ''}>${s.mic === 'asking' ? `<i class="spin-dot"></i>${esc(T('micAsk'))}` : `<i class="ico ico-s">${I.mic}</i>${esc(T('setupMic'))}`}</button>` +
      `<p class="setup-n">${esc(msg)}</p></div>`;
  } else {
    if (pending) h += `<p class="setup-n">${esc(T('b_first'))}</p>`;
    const lv = rec === 'countdown' ? 'idle' : s.level || 'good';
    h += `<div class="recwrap lv-${lv}" style="--lvl:${(s.lvl || 0).toFixed(2)}"><i class="halo"></i><button class="recbtn${rec === 'recording' ? ' stop' : ''}" data-act="record"${ro ? ' disabled' : ''} title="${esc(rec === 'recording' ? T('stop') : T('record'))} (R)">` +
      `${rec === 'recording' ? `<i class="ico">${I.stop}</i>` : '<i class="recdot"></i>'}</button></div>`;
    const devOpts = (s.devices || []).map((x) => ({ value: x, label: x }));
    h += `<div class="mic-r"><div class="reclabel">${esc(rec === 'recording' ? T('stop') : T('record'))} <kbd>R</kbd></div>` +
      `<div class="lvtext lv-${lv}"><i class="lvdot"></i>${esc(rec === 'countdown' ? T('getReady') : T(`lv_${s.level || 'good'}`))}</div></div>` +
      `<div class="dev" title="${esc(T('mic'))}"><i class="ico ico-xs">${I.mic}</i>${renderDropdown({ id: 'dd-device', dataCh: 'device', value: s.device || devOpts[0]?.value || '', options: devOpts, disabled: ro || rec !== 'idle', ariaLabel: T('mic'), className: 'dd-device' })}</div>`;
  }
  h += '</div>';

  // the tape
  h += '<div class="d-tape">';
  h += `<div class="tape-top"><span class="sticker">${esc(l.id)}${t ? ` · ${esc(T('take'))} ${tIndex + 1}${fav === t.id ? ' ★' : ''}` : ''}</span><span class="takes">`;
  takes.forEach((tk, i) => {
    h += `<span class="tk${tk.id === t?.id ? ' on' : ''}${fav === tk.id ? ' fav' : ''}${published === tk.id ? ' pub' : ''}"><button class="tk-b" data-act="take" data-id="${esc(tk.id)}"${rec !== 'idle' ? ' disabled' : ''}${published === tk.id ? ` title="${esc(T('in_pack'))}"` : ''}>${esc(T('take'))} ${i + 1}${published === tk.id ? '<i class="pubdot"></i>' : ''}</button>` +
      `<button class="tk-s" data-act="fav" data-id="${esc(tk.id)}" title="${esc(fav === tk.id ? T('favTake') : T('starTake'))}"${ro ? ' disabled' : ''}>${fav === tk.id ? I.starF : I.star}</button>` +
      `<button class="tk-x" data-act="take-del" data-id="${esc(tk.id)}" title="${esc(published === tk.id ? T('take_in_use') : T('take_remove'))}"${ro || rec !== 'idle' ? ' disabled' : ''}>×</button></span>`;
  });
  h += `<button class="tk-imp" data-act="import"${ro || rec !== 'idle' ? ' disabled' : ''} title="${esc(T('drop'))}"><i class="ico ico-xs">${I.importI}</i>${esc(T('import'))}</button></span></div>`;
  h += `<div class="tape" id="tape"><i class="reel l" aria-hidden="true">${reel()}</i><div class="wave" id="wave">`;
  if (rec === 'countdown') h += `<div class="count"><b>${s.count}</b><span>${esc(T('silentCount'))}</span></div>`;
  else if (rec === 'recording') h += `<div class="recbadge"><i></i>${esc(T('recording'))} <span class="mono" id="rec-t">${fmtTime(s.recElapsed || 0)}</span></div>${waveSvg(s.livePeaks, false)}`;
  else if (t) {
    const total = t.sourceDurationSec || 1, a = trimS / total, b = trimE / total;
    h += `${waveSvg(s.peaks?.[t.id], t.evenOutVolume)}<i class="trim-dim l" style="width:${(a * 100).toFixed(2)}%"></i><i class="trim-dim r" style="width:${((1 - b) * 100).toFixed(2)}%"></i>` +
      ['s', 'e'].map(which => {
        const start = which === 's', value = start ? trimS : trimE;
        const min = start ? 0 : Math.ceil((trimS + 0.1 - 1e-9) * 1000) / 1000;
        const max = start ? Math.max(0, Math.floor((trimE - 0.1 + 1e-9) * 1000) / 1000) : total;
        return `<i class="handle ${start ? 'l' : 'r'}" data-h="${which}" data-take="${esc(t.id)}" role="slider" tabindex="${ro ? -1 : 0}" aria-disabled="${ro}" aria-label="${esc(T(start ? 'trimStart' : 'trimEnd'))}" aria-valuemin="${min.toFixed(3)}" aria-valuemax="${max.toFixed(3)}" aria-valuenow="${value.toFixed(3)}" aria-valuetext="${esc(T('trimValue', { s: value.toFixed(2) }))}" style="left:${((start ? a : b) * 100).toFixed(2)}%" title="${esc(T('trimTip'))}"></i>`;
      }).join('') +
      `<i class="playhead" id="playhead" style="left:${(a * 100).toFixed(2)}%"></i>`;
  } else h += `<div class="notakes">${esc(pending ? T('b_first') : !featureOn(s, 'dubs') ? T('feat_dubs') : isReadOnly(s) ? T('roNote') : T('noTakes'))}</div>`;
  h += `<div class="dropv">${esc(T('drop'))}</div></div><i class="reel r" aria-hidden="true">${reel()}</i></div>`;

  // controls row
  h += '<div class="tape-ctl">';
  h += `<button class="btn ghost sm playtake${s.playingTake ? ' on' : ''}" data-act="play-take"${!t || rec !== 'idle' ? ' disabled' : ''}><i class="ico ico-s">${s.playingTake ? I.stop : I.play}</i>${esc(s.playingTake ? T('stopTake') : T('playTake'))} <kbd>Space</kbd></button>`;
  const td = rec === 'recording' ? s.recElapsed || 0 : dur;
  h += `<div class="fit">${renderFit(l, td, T)}</div>`;
  h += `<span class="even-help"><label class="tgl" title="${esc(T('evenTip'))}"><input type="checkbox" data-ch="even"${t?.evenOutVolume ? ' checked' : ''}${!t || ro ? ' disabled' : ''}><i class="sw"></i>${esc(T('even'))}</label>${helpButton(T('evenTip'), T('even'))}</span>`;
  h += '</div></div>';

  // origin, consent, retake, taking a take out of the pack
  const inherited = t?.consentSource === 'pack', originDraft = takeOriginDraft(s);
  const chosen = originDraft?.origin || t?.origin;
  const originLabel = (origin) => T(ORIGINS.find(([o]) => o === origin)?.[1] || 'o_own');
  h += `<div class="d-meta"><div class="mk">${esc(T('origin'))}${t ? ` · ${esc(T('take'))} ${tIndex + 1}` : ''}${helpButton(T('consent_text'), T('origin'))}</div>`;
  h += `<div class="origin" role="radiogroup" aria-label="${esc(T('origin'))}">` +
    ORIGINS.map(([o, key]) => `<button role="radio" aria-checked="${chosen === o}" class="og${chosen === o ? ' on' : ''}" data-act="origin" data-id="${o}"${!t || ro || originDraft?.saving ? ' disabled' : ''}>${esc(T(key))}</button>`).join('') + '</div>';
  if (t) {
    h += `<div class="take-origin-state${inherited ? ' pv-inherited' : ''}"><span>${esc(inherited ? `${T('fromPackDefault')}: ${originLabel(t.origin)}` : T('takeOriginSaved', { origin: originLabel(t.origin) }))}</span>` +
      (originDraft ? `<span class="take-origin-pending">${esc(T('takeOriginUnsaved', { origin: originLabel(originDraft.origin) }))}</span>` : '') + '</div>';
  }
  if (originDraft) {
    h += `<label class="ck" title="${esc(T('consent_text'))}"><input type="checkbox" data-ch="take-origin-consent"${originDraft.confirmed ? ' checked' : ''}${ro || originDraft.saving ? ' disabled' : ''}><i class="box">${I.check}</i><span>${esc(T('consent'))}</span></label>` +
      `<button type="button" class="btn peach sm" data-act="take-origin-save"${ro || !originDraft.confirmed || originDraft.saving ? ' disabled' : ''}>${esc(T(originDraft.saving ? 'saving' : 'saveForTake'))}</button>`;
  } else if (!inherited) {
    if (t && t.origin !== 'own') h += `<div class="ai-n"><i class="ico ico-xs">${I.info}</i>${esc(T('aiLabel'))}</div>`;
    h += `<label class="ck" title="${esc(T('consent_text'))}"><input type="checkbox" data-ch="consent"${consentOk(t, s.consentVersion) ? ' checked' : ''}${!t || ro ? ' disabled' : ''}><i class="box">${I.check}</i><span>${esc(T('consent'))}</span></label>`;
  }
  if (t && hasPackVoiceDefault(s) && (!inherited || originDraft)) {
    h += `<button type="button" class="btn ghost xs pv-follow" data-act="take-follow-pack" data-id="${esc(t.id)}"${ro || originDraft?.saving ? ' disabled' : ''}>${esc(T('usePackDefault'))}</button>`;
  }
  if (s.menu === 'consent' && t) {
    const askPackOnce = !originDraft && featureOn(s, 'voiceDefault') && (!hasPackVoiceDefault(s) || s.firstPackTakeId === t.id);
    h += `<div class="menu consent-menu" role="dialog" data-motion="pop" aria-label="${esc(T('consent_title'))}"><div class="menu-k">${esc(T(askPackOnce ? 'consentPackTitle' : 'consent_title'))}</div>` +
      (askPackOnce ? `<div class="cm-sub">${esc(T('consentPackOnce'))}</div><div class="origin cm-origin" role="radiogroup">${ORIGINS.map(([o, key]) => `<button class="og${t.origin === o ? ' on' : ''}" data-act="origin" data-id="${o}">${esc(T(key))}</button>`).join('')}</div>` : '') +
      `<p class="cm-t">${esc(T('consent_text'))}</p>` +
      `<div class="np-b"><button class="btn ghost" data-act="consent-cancel">${esc(T('cancel'))}</button><button class="btn peach" data-act="consent-confirm">${esc(T('consent_confirm'))}</button></div></div>`;
  }
  h += `<button class="retake${l.status === 'needs-retake' ? ' on' : ''}" data-act="retake"${ro || !featureOn(s, 'workflow') || (!takes.length && l.status !== 'needs-retake') ? ' disabled' : ''}><i class="ico ico-s">${I.flag}</i>${esc(T('retake'))}</button>`;
  if (t && published === t.id && !ro) h += `<button class="btn ghost sm unpub" data-act="unpublish">${esc(T('unpublish'))}</button>`;
  h += '</div></div></div><i class="foot l" aria-hidden="true"></i><i class="foot r" aria-hidden="true"></i>';
  return h;
}
