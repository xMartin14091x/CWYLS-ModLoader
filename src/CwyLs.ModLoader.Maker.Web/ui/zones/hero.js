// Zone 5 (hero actions + spoken line + keys), the banners and the ground strip. Pure render: (state, ctx) -> HTML string.
import { esc } from '../util.js';
import { I } from './icons.js';
import { HOUSE } from './art.js';
import { isReadOnly, packOf, canPlay, canDub, currentTake, featureOn, missingFeature, langMismatch } from '../selectors.js';
import { linkState } from '../api/model.js';

function ago(at, T) {
  const s = Math.max(0, Math.round((Date.now() - at) / 1000));
  if (s < 5) return T('agoNow');
  if (s < 90) return T('agoSec', { n: s });
  return T('agoMin', { n: Math.round(s / 60) });
}

/** A language's display name from the strings, else its code in capitals (the game may report a language the page has no name for). */
export function langName(T, code) {
  const k = `lang_${code}`, v = T(k);
  return v === k ? String(code).toUpperCase() : v;
}

export function renderHero(s, ctx) {
  const { T } = ctx, l = s.line, p = packOf(s), ro = isReadOnly(s), link = linkState(s.status, s.conn);
  if (!l) return '';
  const noPlay = !canPlay(s) ? T('feat_playback') : '';
  const noMine = !canPlay(s) && !featureOn(s, 'dubs') ? T('feat_playback') : '';
  const disMine = link === 'off' ? T('dis_off') : noMine || (p?.restartRequired ? T('dis_first') : ro ? T('dis_ro') : '');
  const disOrig = link === 'off' ? T('dis_off') : noPlay;
  const subtitleOnly = !currentTake(s) && !l.ownedDub;
  const btn = (kind) => {
    const mine = kind === 'mine', dis = mine ? disMine : disOrig, busy = s.speaking === kind, wait = s.waiting === kind;
    const sub = dis || (wait ? T('waiting') : busy ? T('speaking') : T(mine ? subtitleOnly ? 'play_subtitle_sub' : 'play_mine_sub' : 'play_orig_sub'));
    return `<button class="hb hb-${kind}${busy ? ' busy' : ''}${wait ? ' wait' : ''}" data-act="play-${kind}" title="${esc(sub)}"${dis || s.playing ? ' disabled' : ''}>` +
      `<span class="hb-ico">${wait ? I.pause : I.game}</span><span class="hb-t"><b>${esc(T(mine ? 'play_mine' : 'play_orig'))}</b></span>` +
      `<kbd>${mine ? 'T' : '⇧T'}</kbd>${busy ? `<i class="hb-prog" style="animation-duration:${(s.speakDur || 2).toFixed(2)}s"></i>` : ''}</button>`;
  };
  let h = `<div class="hero-b">${btn('orig')}${btn('mine')}</div>${langNote(s, T)}`;
  const last = s.last, ll = last && s.byId.get(last.id);
  if (ll) {
    const lt = ll.text && ll.text.trim() ? ll.text.replace(/\n/g, ' ') : ll.ja;
    const open = ll.id === s.lineId ? `<span class="said-here">${esc(T('said_here'))}</span>` : `<button class="said-open" data-act="open-said" data-id="${esc(ll.id)}">${esc(T('said_open'))}</button>`;
    h += `<div class="said panel-soft"><span class="said-k">${esc(T('lastSaid'))}</span><span class="said-t">“${esc(lt)}”</span>` +
      `<span class="said-m"><i class="vdot ${esc(last.variant)}"></i>${esc(T(last.variant === 'dub' ? 'v_dub' : 'v_orig'))} · <span id="said-ago">${esc(ago(last.at, T))}</span>${open}</span></div>`;
  } else if (last) {
    h += `<div class="said panel-soft"><span class="said-k">${esc(T('lastSaid'))}</span><span class="said-t">${esc(last.id)}</span><span class="said-m">${esc(T('said_unknown'))}</span></div>`;
  }
  return h;
}

function quickFlowCard(s, T) {
  const l = s.line;
  if (!l) return '';
  const dubOk = canDub(s) && !!l.voice;
  const take = currentTake(s);
  const rec = s.rec || 'idle';
  const recDis = !dubOk || (rec === 'idle' && (l.takes?.length || 0) >= 3);
  const keepDis = !dubOk || !take || !take.sourcePresent || rec !== 'idle';
  const recLbl = rec === 'recording' ? T('stop') : rec === 'countdown' ? T('cancel') : T('record');
  const recIco = rec === 'idle' ? I.mic : I.stop;
  const stTxt = !l.voice ? T('vf_unvoiced') : (l.ownedDub || l.publishedTakeId) ? T('in_pack') : (l.takes?.length || l.takeCount) > 0 ? `${T('take')} ${l.takes?.length || l.takeCount}/3` : T('qf_unrec');
  const stCls = (l.ownedDub || l.publishedTakeId) ? 'done' : (l.takes?.length || l.takeCount) > 0 ? 'draft' : 'todo';
  return `<div class="qflow panel-soft">` +
    `<div class="qf-head"><span class="qf-k">${esc(T('qflowTitle'))}</span><span class="qf-st ${stCls}">${esc(stTxt)}</span></div>` +
    `<div class="qf-acts">` +
    `<button class="btn ${rec !== 'idle' ? 'peach' : 'ghost'} sm qf-rec" data-act="quick-record"${recDis ? ' disabled' : ''} title="${esc(T('qfRecTip'))}"><i class="ico ico-xs">${recIco}</i><span>${esc(recLbl)}</span></button>` +
    `<button class="btn peach sm qf-keep" data-act="keep-next"${keepDis ? ' disabled' : ''} title="${esc(T('qfKeepTip'))}"><i class="ico ico-xs">${I.check}</i><span>${esc(T('keepNext'))}</span></button>` +
    `<button class="btn ghost sm qf-next" data-act="next-unrecorded" title="${esc(T('nextUnrecTip'))}"><i class="ico ico-xs">${I.nextI}</i><span>${esc(T('nextUnrecShort'))}</span></button>` +
    `</div></div>`;
}

/** The game's language differs from the pack's: a short note in the right column, with the full story as its tooltip. */
function langNote(s, T) {
  const m = langMismatch(s);
  if (!m) return '';
  const v = { game: langName(T, m.game), pack: langName(T, m.pack) };
  const btn = m.switchTo ? `<button class="btn ghost sm" data-act="match-lang" data-id="${esc(m.switchTo)}">${esc(T('langnote_btn', { game: langName(T, m.switchTo) }))}</button>` : '';
  return `<div class="mir-lang" role="note" title="${esc(T('langnote_tip', v))}"><i class="ico ico-xs">${I.warn}</i><span>${esc(T('langnote', v))}</span>${btn}</div>`;
}

function keysCard(T) {
  return `<div class="keys panel-soft"><span><kbd>↑</kbd><kbd>↓</kbd>${esc(T('k_line'))}</span><span title="${esc(T('doneKeyTip'))}"><kbd>↵</kbd><kbd>⌘↵</kbd>${esc(T('k_done'))}</span>` +
    `<span><kbd>R</kbd>${esc(T('k_rec'))}</span><span><kbd>Space</kbd>${esc(T('k_play'))}</span>` +
    `<span class="k-wide"><kbd>T</kbd>${esc(T('k_game'))}<span class="k-alt"><kbd>⇧T</kbd>${esc(T('k_orig'))}</span></span></div>`;
}

/** @returns {{kind:string, html:string}} kind is '' when no banner is shown */
export function renderBanner(s, ctx) {
  const { T } = ctx, p = packOf(s), link = linkState(s.status, s.conn);
  if (s.fatal) return { kind: 'off', html: `<i class="b-ico">${I.plug}</i><div class="b-t"><b>${esc(T(s.fatal.titleKey))}</b><span>${esc(T(s.fatal.bodyKey))}</span></div>` };
  if (link === 'unauthorized') return { kind: 'off', html: `<i class="b-ico">${I.plug}</i><div class="b-t"><b>${esc(T('b_unauth_t'))}</b><span>${esc(T('b_unauth'))}</span></div>` };
  if (link === 'off') return { kind: 'off', html: `<i class="b-ico">${I.plug}</i><div class="b-t"><b>${esc(T('b_off_t'))}</b><span>${esc(T('b_off'))}</span></div>` };
  if (p && p.restartRequired) return { kind: 'first', html: `<i class="b-ico">${I.spark}</i><div class="b-t"><b>${esc(T('b_first_t', { pack: p.name }))}</b><span>${esc(T('b_first'))}</span></div>` };
  const miss = p && p.editable ? missingFeature(s) : null;
  if (miss) return { kind: 'feat', html: `<i class="b-ico">${I.info}</i><div class="b-t"><b>${esc(T('feat_title'))}</b><span>${esc(T(`feat_${miss}`))}</span></div>` };
  if (p && !p.editable) {
    const why = p.readOnlyKey ? T(p.readOnlyKey) : p.readOnlyText || T('ro_other');
    const mine = s.packs.find((x) => x.editable);
    return { kind: 'ro', html: `<i class="b-ico">${I.lock}</i><div class="b-t"><b>${esc(T('b_ro_t', { pack: p.name }))}</b><span>${esc(why)} · ${esc(T('b_ro'))}</span></div>` +
      `<div class="b-a"><button class="btn ghost sm" data-act="new-pack-open">${esc(T('createPack'))}</button>${mine ? `<button class="btn peach sm" data-act="pick-pack" data-id="${esc(mine.id)}">${esc(T('b_back'))}</button>` : ''}</div>` };
  }
  return { kind: '', html: '' };
}

export function renderGround() {
  return `<i class="house" aria-hidden="true">${HOUSE}</i>`;
}
