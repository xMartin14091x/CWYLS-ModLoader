// Zone 1: the top bar. Pure render: (state, ctx) -> HTML string. ctx = {T, lang}.
import { esc, fmtN } from '../util.js';
import { I } from "./icons.js";
import { MOON } from "./art.js";
import { packOf, featureOn, canCreatePacks, canExport, canVoiceDefault, hasPackVoiceDefault, langMismatch } from '../selectors.js';
import { langName } from './hero.js';
import { renderDropdown } from './dropdown.js';
import { renderHelpPopover } from './tour.js';
import { linkState } from '../api/model.js';

const ORIGINS = [['own', 'o_own'], ['ai-assisted', 'o_assist'], ['ai-generated', 'o_gen']];

const LINK_CLASS = { ok: 'ok', paused: 'paused', unfocused: 'paused', stale: 'paused', off: 'off', unauthorized: 'off' };

function progItem(kind, label, n, tot) {
  const pct = tot ? Math.min(100, 100 * n / tot) : 0;
  return `<div class="prog-i prog-${kind}" title="${esc(`${label} ${fmtN(n)}/${fmtN(tot)}`)}"><span class="k">${esc(label)}</span>` +
    `<span class="v"><b>${fmtN(n)}</b><span class="of">/${fmtN(tot)}</span></span><i class="bar"><i style="width:${pct.toFixed(1)}%"></i></i></div>`;
}

export function renderLinkPill(s, T) {
  const link = linkState(s.status, s.conn), cls = LINK_CLASS[link], linkTxt = T(`link_${link}`);
  return `<span class="link link--${cls}" title="${esc(linkTxt)}"><i class="ldot"></i><span class="link-t">${esc(linkTxt)}</span></span>`;
}

function keysPopover(T) {
  return `<div class="menu keys-menu" role="dialog" data-motion="pop" aria-label="${esc(T('keysTitle'))}">` +
    `<div class="menu-k">${esc(T('keysTitle'))}</div>` +
    `<div class="keys-list">` +
    `<div class="k-row"><span><kbd>↑</kbd> <kbd>↓</kbd></span><span>${esc(T('k_line'))}</span></div>` +
    `<div class="k-row"><span><kbd>↵</kbd> / <kbd>⌘↵</kbd></span><span>${esc(T('k_done'))}</span></div>` +
    `<div class="k-row"><kbd>R</kbd><span>${esc(T('k_rec'))}</span></div>` +
    `<div class="k-row"><kbd>Space</kbd><span>${esc(T('k_play'))}</span></div>` +
    `<div class="k-row"><kbd>T</kbd><span>${esc(T('k_game'))}</span></div>` +
    `<div class="k-row"><kbd>⇧T</kbd><span>${esc(T('k_orig'))}</span></div>` +
    `</div></div>`;
}

export function renderProg(s, T) {
  const tot = s.overall;
  return `${progItem('sub', T('subs'), tot.subtitlePresent, tot.totalLines)}${progItem('dub', T('dubs'), tot.dubPresent, tot.dubbableLines)}`;
}

/** An amber mark beside the pack language while the game is in another language (the full explanation is its tooltip and the right-column note). */
export function langBadge(s, T) {
  const m = langMismatch(s);
  if (!m) return '';
  const v = { game: langName(T, m.game), pack: langName(T, m.pack) };
  return `<i class="ico ico-xs langwarn" role="img" aria-label="${esc(T('langnote_tip', v))}" title="${esc(T('langnote_tip', v))}">${I.warn}</i>`;
}

function packMenu(s, T) {
  let h = '<div class="menu pack-menu" role="menu" data-motion="pop">';
  if (s.menu === 'newpack') {
    const npLangOpts = ['th', 'vi', 'id', 'es', 'fr', 'de'].map((lg) => ({ value: lg, label: T(`lang_${lg}`) }));
    h += `<div class="np"><div class="menu-k">${esc(T('createPack'))}</div>` +
      `<label class="np-f"><span>${esc(T('newPackName'))}</span><input id="np-name" value="${esc(T('newPackDefault'))}" maxlength="48"></label>` +
      `<div class="np-f"><span>${esc(T('newPackLang'))}</span>${renderDropdown({ id: 'dd-top-np-lang', selectId: 'np-lang', value: 'th', options: npLangOpts, ariaLabel: T('newPackLang'), className: 'dd-np-lang' })}</div>` +
      `<div class="np-b"><button class="btn ghost" data-act="np-cancel">${esc(T('cancel'))}</button><button class="btn peach" data-act="np-create">${esc(T('create'))}</button></div></div></div>`;
    return h;
  }
  h += `<div class="menu-k">${esc(T('yourPacks'))}</div>`;
  for (const p of s.packs.filter((x) => x.editable || x.restartRequired)) {
    h += `<button class="mi${p.id === s.packId ? ' on' : ''}" data-act="pick-pack" data-id="${esc(p.id)}"><span class="mi-n">${esc(p.name)}` +
      (p.restartRequired ? ` <em class="tag-new">${esc(T('pending'))}</em>` : '') + '</span>' +
      `<span class="mi-m">v${esc(p.version)} · ${p.languages.map((l) => esc(langName(T, l))).join(', ')}</span>` +
      (p.id === s.packId ? `<i class="ico ico-s mi-c">${I.check}</i>` : '') + '</button>';
  }
  h += `<button class="mi mi-new" data-act="new-pack"${canCreatePacks(s) ? '' : ` disabled title="${esc(T('feat_newpacks'))}"`}><i class="ico ico-s">${I.plus}</i><span class="mi-n">${esc(T('createPack'))}</span></button>`;
  h += `<button class="mi mi-act" data-act="open-packs"><i class="ico ico-s">${I.packs}</i><span class="mi-n">${esc(T('packsView'))}</span></button>`;
  h += `<button class="mi mi-act" data-act="open-export"${canExport(s) ? '' : ' disabled'}><i class="ico ico-s">${I.exportI}</i><span class="mi-n">${esc(T('exportPack'))}</span></button>`;
  const ro = s.packs.filter((x) => !x.editable && !x.restartRequired);
  if (ro.length) h += `<div class="menu-k">${esc(T('readOnlyPacks'))}</div>`;
  for (const p of ro) {
    const why = p.readOnlyKey ? T(p.readOnlyKey) : p.readOnlyText || T('ro_other');
    h += `<button class="mi ro${p.selectable ? '' : ' dis'}${p.id === s.packId ? ' on' : ''}"${p.selectable ? ` data-act="pick-pack" data-id="${esc(p.id)}"` : ' disabled aria-disabled="true"'}>` +
      `<span class="mi-n"><i class="ico ico-s">${I.lock}</i>${esc(p.name)}</span><span class="mi-m">${esc(why)}</span></button>`;
  }
  return `${h}</div>`;
}

export function renderTopbar(s, { T, lang }) {
  const p = packOf(s);
  const langs = p?.languages?.length ? p.languages : ['th'];
  const open = s.menu === 'pack' || s.menu === 'newpack';
  const page = s.page || 'studio';
  const tabs = [
    ['studio', T('tab_studio')],
    ['lines', T('tab_lines')],
    ['packs', T('tab_packs')],
    ['export', T('tab_export')],
    ['settings', T('tab_settings')]
  ];
  let h = `<div class="brand"><img class="logo" src="./ui/art/pack-maker-icon.svg" alt="">` +
    `<div class="brand-t"><div class="brand-name">Pack Maker</div><div class="brand-studio sr-only">${esc(T('studio'))}</div></div></div>`;
  h += `<nav class="top-nav top-tabs" role="tablist" aria-label="Pack Maker">` +
    tabs.map(([id, label]) => `<button type="button" class="top-tab${page === id ? ' on' : ''}" role="tab" aria-selected="${page === id}" data-act="page-tab" data-id="${id}">${esc(label)}</button>`).join('') +
    `</nav>`;
  h += `<div class="top-r">`;
  h += `<div class="prog" role="group" aria-label="${esc(`${T('subs')} · ${T('dubs')}`)}">${renderProg(s, T)}</div>`;
  h += `<div class="picker${open ? ' open' : ''}"><button class="pick" data-act="pack-menu" aria-haspopup="true" aria-expanded="${open}" title="${esc(T('writingTo'))}">` +
    `<span class="pick-v">${p && !p.editable ? `<i class="ico ico-s">${I.lock}</i>` : ''}${esc(p?.name || '—')}` +
    `${p?.restartRequired ? ` <em class="tag-new">${esc(T('pending'))}</em>` : ''}</span><i class="ico ico-s chev">${I.chev}</i></button>${open ? packMenu(s, T) : ''}</div>`;
  const packLangOpts = langs.map((lg) => ({ value: lg, label: langName(T, lg) }));
  h += `<div class="plang" title="${esc(T('packLang'))}">${renderDropdown({ id: 'dd-packlang', dataCh: 'packlang', value: s.packLang, options: packLangOpts, disabled: langs.length < 2, ariaLabel: T('packLang'), className: 'dd-packlang' })}${langBadge(s, T)}</div>`;
  h += renderLinkPill(s, T);
  const gameLang = s.status?.gameLanguage;
  h += `<div class="uilang" role="group" aria-label="${esc(T('uiLang'))}" title="${esc(`${T('uiLang')} · ${T('followsGame')}`)}">` +
    `<button data-act="ui-th" class="${lang === 'th' ? 'on' : ''}${gameLang === 'th' ? ' game' : ''}">ไทย</button>` +
    `<button data-act="ui-en" class="${lang === 'en' ? 'on' : ''}${gameLang && gameLang !== 'th' ? ' game' : ''}">EN</button></div>`;
  const kOpen = s.menu === 'keys';
  h += `<div class="keys-w${kOpen ? ' open' : ''}">` +
    `<button type="button" class="btn ghost sm top-act keys-btn${kOpen ? ' on' : ''}" data-act="keys-menu" aria-haspopup="dialog" aria-expanded="${kOpen}" aria-label="${esc(T('helpTitle'))}" title="${esc(T('helpTitle'))}">?</button>` +
    (kOpen ? renderHelpPopover(s, T, lang) : '') +
    (s.guideHint && !kOpen ? `<span class="guide-hint" role="status">${esc(T('helpHint'))}</span>` : '') +
    `</div>`;
  h += `</div>`;
  return h;
}
