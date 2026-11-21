// Pack Maker Studio v2: builds the shell, connects the port, paints the five zones and routes DOM events to actions.js.
// M3 scope: browse and edit text. Subtitles autosave (logic/draft.js), status/star/needs-retake, history and revert, pack
// creation, game status from events, reconnect. Takes, recording, publishing and Play are M4 (see PLAN.md §2).
import { createStore } from './store.js';
import { makeT, STRINGS, effectiveLang, storedChoice, storeChoice } from './i18n/i18n.js';
import { connect, StudioError } from './api.js';
import { errorKey } from './api/model.js';
import { createActions } from './actions.js';
import { createClientLog, safePath } from './clientlog.js';
import { renderSky } from './zones/art.js';
import { renderTopbar, renderLinkPill, renderProg, langBadge } from './zones/topbar.js';
import { renderLeft, renderSceneItems, renderRows, sceneLinesOf, scrollTargetFor, currentSceneId, renderPageView, renderVoiceRows, voiceLinesFor, voiceRowTop, VB_ROW_PX } from './zones/scenes.js';
import { renderCard, renderLive, cardKey } from './zones/line-card.js';
import { renderHero, renderBanner, renderGround } from './zones/hero.js';
import { renderDeck, waveSvg, renderFit } from './zones/dub-deck.js';
import { renderModal } from './zones/packs-modal.js';
import { bindDropdownEvents, syncDropdownFromSelect, closeAllDropdowns, openPop, closePop, motionSmallMs } from './zones/dropdown.js';
import { markPageTourSeen, tourStepsFor, syncTourCompanion, positionTourCompanion, teardownTourCompanion } from './zones/tour.js';
import { helpPage, claimGuideHint } from './zones/guide-context.js';
import { makeDefaultIconPng } from './api/dev-phase-c.js';
import { createCapture } from './audio/capture.js';
import { createPlayer } from './audio/player.js';
import { decodeFile } from './audio/import.js';
import { BUILTIN_GLOSSARY, insertTerm } from './logic/glossary.js';
import { applyFix } from './logic/qa.js';
import { acceptsSubtitleEdit } from './logic/thai.js';
import { mulberry, $, esc, fmtTime } from './util.js';
import { I } from './zones/icons.js';
import { bindHelpPopovers } from './zones/help-popover.js';
import { rowsOf, visibleLines, editorText, currentTake, takeOriginDraft, packOf, canCreatePacks, canExport, canPlay, canDub, isReadOnly, featureOn, displayGroups } from './selectors.js';
import { loadSettings } from './logic/progress.js';
import { linkState } from './api/model.js';
import { suggestPackName, suggestPackId } from './logic/pack-naming.js';
import { initMotion, setMotionSettings, syncParallaxToPage, transitionPage, rebindIncomingStream } from './motion.js';

const SHELL = `
  <div class="sky" id="sky" aria-hidden="true"><svg class="stars" id="stars"></svg><svg class="hills" id="hills" preserveAspectRatio="none"></svg></div>
  <div class="app" id="studio">
    <header class="top" id="top"></header>
    <main class="main" id="main">
      <div class="banner" id="banner" role="status" hidden></div>
      <aside class="left panel" id="left"></aside>
      <section class="studio-card card" id="studio-card">
        <div class="card-body" id="card"></div>
      </section>
      <aside class="studio-inspector panel" id="studio-inspector">
        <div class="deck-inline" id="deck"></div>
        <div class="card-bottom">
          <div class="cfoot" id="cfoot"></div>
          <div class="hero-inline" id="hero"></div>
        </div>
      </aside>
      <section class="page-view panel" id="page-view" hidden></section>
    </main>
    <footer class="ground" id="ground"></footer>
  </div>
  <div class="modal-backdrop" id="modal" hidden></div>
  <div class="tour-layer" id="tour-layer" hidden></div>
  <div class="toasts" id="studio-toasts" aria-live="polite"></div>
  <input type="file" id="file" accept="audio/*,.wav" hidden>
  <input type="file" id="icon-file" accept="image/png" hidden>`;

const DEVICES = ['MacBook Air Microphone', 'USB Microphone (Yeti Nano)', 'AirPods Pro'];

function initialState() {
  return {
    ready: false, fatal: null, mode: 'dev', fixture: 'normal', page: 'studio',
    uiManual: storedChoice(), gameLang: null,
    conn: { connected: false, unauthorized: false }, status: null,
    packs: [], packId: null, packLang: 'th',
    groups: [], overall: { totalLines: 0, dubbableLines: 0, subtitlePresent: 0, dubPresent: 0 }, catalogRevision: null,
    lines: [], byId: new Map(), loadingLines: true, linesError: null, linesAttempt: 0, warnVersion: 0,
    view: { group: null, filter: 'all', q: '', groupByStatus: false },
    lineId: null, line: null, draft: null, history: null, saveNote: null, saveLive: null, menu: null, cpsAgainst: 'auto', scrollTo: null,
    glossary: BUILTIN_GLOSSARY,
    // dub deck: UI-only for now (recording and takes are M4)
    mic: 'none', micError: null, device: '', devices: [], deviceIds: [], rec: 'idle', level: 'idle', lvl: 0, count: 3, recElapsed: 0, livePeaks: [],
    features: null, consentVersion: null, trimDraft: null, playing: false,
    selTakeId: null, peaks: {}, playingTake: false, dragOver: false, speaking: null, waiting: null, speakDur: 2, last: null,
    modal: null, packsSub: 'list', creatingPack: false, packInfos: {}, packIcons: {}, packProgress: {}, newPackForm: null,
    exportPreview: null, exportResult: null, exportLoading: false, exportBusy: false, exportBump: 'patch', exportNotes: '',
    lastDownloadedZip: null, sessionConsent: false, sessionOrigin: 'own', pendingKeepNext: false, iconTargetPackId: null,
    voiceDefault: null, packVoices: {}, packVoiceDraft: 'own', takeOverrides: {}, unconfirmedCount: 0, promptedPackVoice: {}, firstPackTakeId: null,
    tour: null, helpTab: 'guide', guideHint: false,
    settings: loadSettings(),
    motion: initMotion()
  };
}

const uiLangOf = (s) => effectiveLang(s.status?.gameLanguage || s.gameLang, s.uiManual);

export async function start({ search = location.search } = {}) {
  // take over the page from the v1 shell (boot.js only loads this module when v2 was chosen)
  for (const sel of ['#app', '#overlay', '.skip']) document.querySelector(sel)?.remove();
  document.getElementById('toasts')?.remove();
  document.body.insertAdjacentHTML('afterbegin', SHELL);
  bindHelpPopovers();

  const store = createStore(initialState());
  const params = new URLSearchParams(search);
  if (params.get('lang') === 'th' || params.get('lang') === 'en') store.set({ uiManual: params.get('lang') }); // preview/test override, not stored
  if (['studio', 'lines', 'packs', 'export', 'settings'].includes(params.get('page'))) store.set({ page: params.get('page') });
  const hasToken = !!document.querySelector('meta[name="cwyls-maker-token"]');
  let ctxCache = null;
  const ctx = (s) => {
    const lang = uiLangOf(s);
    if (!ctxCache || ctxCache.lang !== lang || ctxCache.glossary !== s.glossary) ctxCache = { lang, T: makeT(lang), strings: STRINGS[lang], glossary: s.glossary };
    return ctxCache;
  };

  function toast(key, kind = 'ok', vars) {
    const box = document.getElementById('studio-toasts'), n = document.createElement('div');
    n.className = `toast ${kind === 'warn' ? 'warn' : ''}`;
    n.innerHTML = `<i class="ico ico-s">${kind === 'warn' ? I.warn : I.check}</i>${esc(ctx(store.get()).T(key, vars))}`;
    box.appendChild(n);
    setTimeout(() => n.classList.add('out'), 2400);
    setTimeout(() => n.remove(), 2900);
  }

  // ---------- painting ----------
  const memo = {};
  const changed = (name, deps) => { const prev = memo[name]; memo[name] = deps; return !prev || deps.some((d, i) => d !== prev[i]); };
  const el = (id) => document.getElementById(id);
  const editorSize = new ResizeObserver(() => autosize($('#ed')));
  let paintQueued = false;
  function paintSoon() {
    if (paintQueued) return;
    paintQueued = true;
    (globalThis.requestAnimationFrame || setTimeout)(() => { paintQueued = false; paint(); });
  }
  store.subscribe(paintSoon);

  const packsKey = (s) => s.packs.map((p) => `${p.id}:${p.name}:${p.version}:${p.editable}:${p.restartRequired}:${p.selectable}:${p.readOnlyKey || ''}:${p.readOnlyText || ''}:${(p.languages || []).join(',')}`).join('|');
  const totKey = (s) => `${s.overall?.subtitlePresent}/${s.overall?.totalLines}:${s.overall?.dubPresent}/${s.overall?.dubbableLines}`;
  const groupsKey = (s) => s.groups.map((g) => `${g.id}:${g.progress?.subtitlePresent}:${g.progress?.dubPresent}:${g.progress?.totalLines}:${g.progress?.dubbableLines}`).join('|');
  const takesKey = (l) => (l?.takes || []).map((t) => `${t.id}:${t.trimStartSec}:${t.trimEndSec}:${t.sourceDurationSec}:${t.evenOutVolume}:${t.origin}:${t.consent?.confirmed}:${t.consent?.statementVersion || ''}:${t.consentSource || 'take'}`).join('|');
  const voiceKey = (s) => `${s.voiceDefault?.revision || ''}:${s.voiceDefault?.origin || ''}:${s.voiceDefault?.consent?.confirmed || false}:${s.packVoiceDraft || ''}:${s.unconfirmedCount || 0}:${Object.keys(s.takeOverrides || {}).join(',')}`;
  let hintChecked = false;
  let helpReturnFocus = false;
  let closingTourToken = 0; // bumped whenever a guide opens or closes, so a stale close animation cannot clear a newer guide

  function paint() {
    const s = store.get(), c = ctx(s);
    document.documentElement.lang = c.lang;
    document.body.classList.toggle('ui-en', c.lang === 'en');
    let skyDirty = false;
    const page = s.page || 'studio';
    const isStudio = page === 'studio';
    if (s.ready && !hintChecked) {
      hintChecked = true;
      s.guideHint = params.get('notour') !== '1' && claimGuideHint();
    }
    if (s.tour && s.tour.page !== helpPage(s)) {
      ++closingTourToken;
      s.tour = null;
    }
    const openMenu = s.menu === 'pack' || s.menu === 'newpack' || s.menu === 'keys' ? s.menu : null;
    if (changed('top', [c.lang, page, s.packsSub, s.helpTab, s.guideHint, packsKey(s), s.packId, s.packLang, openMenu, canCreatePacks(s), canExport(s), s.settings?.autoDoneOnNext, s.settings?.jumpAfterDone, voiceKey(s)])) {
      const active = document.activeElement;
      const helpFocus = active?.closest?.('.help-menu') ? { act: active.dataset.act, id: active.dataset.id } : null;
      el('top').innerHTML = renderTopbar(s, c);
      if (openMenu === 'keys') {
        const selector = helpFocus?.act ? `[data-act="${helpFocus.act}"]${helpFocus.id ? `[data-id="${helpFocus.id}"]` : ''}` : '[data-help-tab][aria-selected="true"]';
        el('top').querySelector(selector)?.focus({ preventScroll: true });
      } else if (helpReturnFocus) {
        helpReturnFocus = false;
        el('top').querySelector('[data-act="keys-menu"]')?.focus({ preventScroll: true });
      }
      memo.topLive = [linkState(s.status, s.conn), s.status?.gameLanguage || '', totKey(s)];
    } else if (changed('topLive', [linkState(s.status, s.conn), s.status?.gameLanguage || '', totKey(s)])) {
      paintTopLive(s, c);
    }
    const banner = renderBanner(s, c);
    if (changed('banner', [banner.kind, banner.html])) { const b = el('banner'); b.hidden = !banner.kind; b.className = `banner${banner.kind ? ` b-${banner.kind}` : ''}`; b.innerHTML = banner.html; el('main').classList.toggle('has-banner', !!banner.kind); }
    el('main').classList.toggle('is-page', !isStudio);
    el('left').hidden = !isStudio;
    el('studio-card').hidden = !isStudio;
    el('studio-inspector').hidden = !isStudio;
    el('studio-inspector').setAttribute('aria-label', c.T('inspector'));
    el('page-view').hidden = isStudio;
    if (!isStudio) {
      if (changed('pageView', [c.lang, page, s.packsSub, s.modal, s.lines, s.view, s.lineId, groupsKey(s), s.packLang, s.warnVersion, packsKey(s), s.packId, s.packInfos, s.packIcons, s.packProgress, s.newPackForm, s.creatingPack, s.exportPreview, s.exportResult, s.exportLoading, s.exportBusy, s.exportBump, canCreatePacks(s), canExport(s), voiceKey(s), s.settings?.autoDoneOnNext, s.settings?.jumpAfterDone, s.motion?.mode, s.motion?.pageMs, s.motion?.smallMs])) {
        const activeQ = document.activeElement?.dataset?.in === 'q' ? { pos: document.activeElement.selectionStart } : null;
        const voiceList = snapshotVoiceList();
        const ae = document.activeElement;
        const focusKey = ae && ae !== document.body && el('page-view').contains(ae) && ae.dataset?.act ? { act: ae.dataset.act, id: ae.dataset.id } : null;
        el('page-view').innerHTML = renderPageView(s, c);
        if (focusKey) {
          // Buttons that change a setting repaint the page: keep the keyboard where it was.
          const again = [...el('page-view').querySelectorAll('[data-act]')].find((n) => n.dataset.act === focusKey.act && n.dataset.id === focusKey.id);
          again?.focus?.({ preventScroll: true });
        }
        el('vb-list')?.addEventListener('scroll', () => paintVoiceRowsSoon(), { passive: true });
        // Establish the full scroll height before restoring an offset on the new list.
        paintVoiceRows();
        if (voiceList && el('vb-list')) {
          el('vb-list').scrollTop = voiceList.top;
          paintVoiceRows(voiceList);
        }
        if (activeQ) {
          const qIn = el('page-view')?.querySelector('input[data-in="q"]');
          if (qIn) { qIn.focus(); try { qIn.setSelectionRange(activeQ.pos, activeQ.pos); } catch {} }
        }
      }
    }
    const curScn = currentSceneId(s);
    if (changed('left', [c.lang, s.packLang, s.ready, groupsKey(s), curScn, s.lines.length])) {
      el('left').innerHTML = renderLeft(s, c);
      el('vlist')?.addEventListener('scroll', () => paintRowsSoon(), { passive: true });
      memo.rows = null;
    }
    if (changed('rows', [c.lang, s.lines, curScn, s.lineId, s.loadingLines, s.linesError, s.linesAttempt, s.packLang, s.warnVersion])) {
      paintRows();
    }
    if (s.scrollTo) {
      const vl = el('vlist'), y = vl && scrollTargetFor(s, vl);
      if (y !== null && y !== undefined) { vl.scrollTop = y; paintRows(); }
      store.set({ scrollTo: null });
    }
    if (s.line) {
      memo.empty = null; // the next pack switch must clear this editor even after an earlier empty paint
      if (changed('cardShell', [cardKey(s, c.lang), s.ready])) { editorSize.disconnect(); el('card').innerHTML = renderCard(s, c); autosize($('#ed')); if (el('editor')) editorSize.observe(el('editor')); memo.cardLive = null; }
      if (changed('cardLive', [c.lang, s.line, s.draft, s.conn.connected, s.saveNote, s.saveLive, s.cpsAgainst, s.packId, s.packLang, s.menu === 'hist', s.history, s.features])) paintLive(s, c);
      const lastLineText = (s.last && s.byId.get(s.last.id)?.text) || '';
      const p = packOf(s);
      if (changed('hero', [c.lang, !!s.line, s.line.id, !!s.line.voice, s.line.ownedDub, s.line.publishedTakeId, s.line.takeCount, takesKey(s.line), s.selTakeId, s.rec, canDub(s), linkState(s.status, s.conn) === 'off', s.status?.gameLanguage || '', s.packId, s.packLang, p?.editable, p?.restartRequired, (p?.languages || []).join(','), canPlay(s), featureOn(s, 'dubs'), s.speaking, s.waiting, s.playing, s.speakDur, s.last?.id || '', s.last?.variant || '', s.last?.at || 0, lastLineText, !!(s.last && s.last.id === s.lineId)])) el('hero').innerHTML = renderHero(s, c);
      if (changed('deck', [c.lang, s.line.id, s.line.durationSec, s.line.status, s.line.publishedTakeId, s.line.favouriteTakeId, takesKey(s.line), canDub(s), isReadOnly(s), p?.restartRequired, featureOn(s, 'dubs'), featureOn(s, 'workflow'), s.rec, s.mic, s.micError, s.count, s.selTakeId, s.playingTake, s.dragOver, s.packId, (s.devices || []).join('|'), s.device, s.menu === 'consent', s.trimDraft, s.consentVersion, voiceKey(s), s.firstPackTakeId, s.takeOriginDraft])) {
        const focused = el('deck').contains(document.activeElement) ? document.activeElement?.dataset?.h : null;
        const takeId = document.activeElement?.dataset?.take;
        el('deck').innerHTML = renderDeck(s, c); memo.deckLive = null; memo.takeWave = null;
        if (focused && currentTake(s)?.id === takeId) el('deck').querySelector(`[data-h="${focused}"]`)?.focus({ preventScroll: true });
      }
      if (changed('deckLive', [c.lang, s.mic, s.rec, s.level, (s.lvl || 0).toFixed(2), (s.recElapsed || 0).toFixed(1), s.livePeaks])) paintDeckLive(s, c);
      // Hydrating a creator take changes only its waveform, preserving focused inspector controls.
      const take = currentTake(s);
      if (changed('takeWave', [s.rec, take?.id, take?.evenOutVolume, take && s.peaks?.[take.id]]) && s.rec === 'idle' && take) {
        const svg = el('wave')?.querySelector('.wsvg');
        if (svg) svg.outerHTML = waveSvg(s.peaks?.[take.id], take.evenOutVolume);
      }
      if (s.playingTake) animatePlayhead();
    } else if (changed('empty', [c.lang, s.ready, s.line])) {
      el('card').innerHTML = ''; el('hero').innerHTML = ''; el('deck').innerHTML = ''; el('cfoot').innerHTML = '';
      memo.cardShell = null; memo.hero = null; memo.deck = null;
    }
    const m = el('modal');
    if (m && !m.hidden) { m.hidden = true; m.innerHTML = ''; }
    if (changed('ground', [c.lang])) { el('ground').innerHTML = renderGround(c); skyDirty = true; }
    if (skyDirty) { renderSky(); syncParallaxToPage(page); }
    document.querySelectorAll('.menu[data-motion="pop"]:not(.c-dd-list):not(.tour-card):not([hidden])').forEach((menuEl) => {
      if (!menuEl.classList.contains('is-opening') && !menuEl.classList.contains('is-open') && !menuEl.classList.contains('is-closing')) {
        openPop(menuEl);
      }
    });
    syncTourCompanion(s, c.T, { lang: c.lang });
    rebindIncomingStream(el('main'));
  }
  function paintTopLive(s, c) {
    const top = el('top');
    if (!top) return;
    const linkEl = top.querySelector('.link');
    if (linkEl) linkEl.outerHTML = renderLinkPill(s, c.T);
    const progEl = top.querySelector('.prog');
    if (progEl) progEl.innerHTML = renderProg(s, c.T);
    const plang = top.querySelector('.plang');
    if (plang) {
      plang.querySelector('.langwarn')?.remove();
      const badge = langBadge(s, c.T);
      if (badge) plang.insertAdjacentHTML('beforeend', badge);
    }
    const gameLang = s.status?.gameLanguage;
    const thBtn = top.querySelector('[data-act="ui-th"]'), enBtn = top.querySelector('[data-act="ui-en"]');
    if (thBtn) thBtn.className = `${c.lang === 'th' ? 'on' : ''}${gameLang === 'th' ? ' game' : ''}`.trim();
    if (enBtn) enBtn.className = `${c.lang === 'en' ? 'on' : ''}${gameLang && gameLang !== 'th' ? ' game' : ''}`.trim();
  }
  function paintDeckLive(s, c) {
    const deck = el('deck');
    if (!deck) return;
    const rec = s.rec || 'idle';
    if (s.mic === 'ready') {
      const lv = rec === 'countdown' ? 'idle' : s.level || 'good';
      const rw = deck.querySelector('.recwrap');
      if (rw) {
        const cls = `recwrap lv-${lv}`;
        if (rw.className !== cls) rw.className = cls;
        rw.style.setProperty('--lvl', (s.lvl || 0).toFixed(2));
      }
      const lt = deck.querySelector('.lvtext');
      if (lt) {
        const cls = `lvtext lv-${lv}`;
        if (lt.className !== cls) lt.className = cls;
        const txt = rec === 'countdown' ? c.T('getReady') : c.T(`lv_${s.level || 'good'}`);
        const html = `<i class="lvdot"></i>${esc(txt)}`;
        if (lt.innerHTML !== html) lt.innerHTML = html;
      }
    }
    if (rec === 'recording') {
      const rt = el('rec-t');
      if (rt) { const t = fmtTime(s.recElapsed || 0); if (rt.textContent !== t) rt.textContent = t; }
      const wsvg = deck.querySelector('#wave .wsvg');
      if (wsvg) wsvg.outerHTML = waveSvg(s.livePeaks, false);
      const fit = deck.querySelector('.fit');
      if (fit && s.line) fit.innerHTML = renderFit(s.line, s.recElapsed || 0, c.T);
    }
  }
  // the playhead of a local take, moved by a frame loop only while one plays
  let playheadRunning = false;
  function animatePlayhead() {
    if (playheadRunning) return;
    playheadRunning = true;
    const step = () => {
      const p = store.get().playingTake;
      const ph = $('#playhead');
      if (!p || !ph) { playheadRunning = false; return; }
      const f = Math.min(1, (Date.now() - p.start) / (p.dur * 1000));
      ph.style.left = `${((p.from + (p.to - p.from) * f) * 100).toFixed(2)}%`;
      requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }
  function paintRows() {
    const s = store.get(), c = ctx(s), vl = el('vlist'), inner = el('vl-inner');
    if (!vl || !inner) return;
    const r = renderRows(s, c, vl);
    inner.style.height = `${r.height}px`;
    inner.innerHTML = r.html;
  }
  let rowsQueued = false;
  function paintRowsSoon() { if (rowsQueued) return; rowsQueued = true; (globalThis.requestAnimationFrame || setTimeout)(() => { rowsQueued = false; paintRows(); }); }
  function snapshotVoiceList() {
    const list = el('vb-list'), active = document.activeElement;
    if (!list) return null;
    return { top: list.scrollTop, focused: list.contains(active), id: active?.closest?.('.vb-row')?.dataset.id };
  }
  function paintVoiceRows(previous = snapshotVoiceList()) {
    const s = store.get(), list = el('vb-list');
    if (!list || (s.page || 'studio') !== 'lines') return;
    list.innerHTML = renderVoiceRows(s, ctx(s), list);
    if (previous?.focused) {
      const row = previous.id && [...list.querySelectorAll('.vb-row')].find((n) => n.dataset.id === previous.id);
      (row || list).focus({ preventScroll: true });
    }
  }
  let voiceRowsQueued = false;
  function paintVoiceRowsSoon() { if (voiceRowsQueued) return; voiceRowsQueued = true; (globalThis.requestAnimationFrame || setTimeout)(() => { voiceRowsQueued = false; paintVoiceRows(); }); }
  function paintLive(s, c) {
    const ed = $('#ed');
    // the draft is the truth: a change that did not come from typing (conflict choice, revert, another tab) is pushed into the box
    if (ed && s.draft && ed.value !== s.draft.text) { ed.value = s.draft.text; autosize(ed); }
    const live = renderLive(s, c, ed ? ed.value : editorText(s));
    const star = $('#star-btn');
    if (star) {
      const starCls = `star-btn${live.star.on ? ' on' : ''}`, starHtml = `<i class="ico ico-s">${live.star.on ? I.starF : I.star}</i>`;
      if (star.className !== starCls) star.className = starCls;
      if (star.title !== live.star.title) star.title = live.star.title;
      if (star.innerHTML !== starHtml) star.innerHTML = starHtml;
    }
    for (const [id, html] of [['first', live.first], ['srcnote', live.src], ['meters', live.meters], ['qa', live.qa], ['cfoot', live.foot]]) { const n = el(id); if (n && n.innerHTML !== html) n.innerHTML = html; }
  }
  function autosize(ed) {
    if (!ed) return;
    // Measure native textarea wrapping without changing the visible box or its layout.
    const c = getComputedStyle(ed), probe = document.createElement('textarea');
    probe.tabIndex = -1; probe.setAttribute('aria-hidden', 'true');
    probe.style.cssText = 'position:absolute;visibility:hidden;pointer-events:none;height:0;min-height:0;padding:0;border:0;box-sizing:content-box;';
    probe.style.width = `${ed.clientWidth - parseFloat(c.paddingLeft) - parseFloat(c.paddingRight)}px`;
    probe.style.font = c.font; probe.style.letterSpacing = c.letterSpacing;
    probe.value = ed.value || ' ';
    ed.parentElement.append(probe);
    const textHeight = probe.scrollHeight;
    probe.remove();
    ed.style.paddingBlock = `${Math.max(8, (ed.clientHeight - textHeight) / 2)}px`;
    ed.style.overflowY = textHeight > ed.clientHeight - 16 ? 'auto' : 'hidden';
  }
  const fatal = (key) => { store.set({ ready: true, loadingLines: false, fatal: { titleKey: key, bodyKey: key.replace(/_t$/, '') } }); paint(); document.body.dataset.studio = 'ready'; };

  // ---------- connect and first load ----------
  let conn;
  try { conn = await connect({ search, hasToken }); } catch { fatal('b_old_t'); return { store }; }
  store.set({ mode: conn.mode, fixture: params.get('fixture') || 'normal' });
  // The page log: console.debug always; posted to the game log while `maker trace on` (or ?debug=1).
  const raw = conn.api.raw;
  const clog = createClientLog({ force: params.get('debug') === '1', send: conn.mode === 'http' && typeof raw.studioClientLog === 'function' ? (entries) => raw.studioClientLog(entries) : null });
  if (conn.mode === 'http') raw.onRequest = (r) => clog.log('api', { ...r, path: safePath(r.path) });
  clog.log('boot', { mode: conn.mode, ui: 2, focused: document.hasFocus?.() ?? null, visible: document.visibilityState || null, agent: navigator.userAgent });
  clog.probe();
  window.addEventListener('focus', () => clog.log('focus', { visible: document.visibilityState || null }));
  window.addEventListener('blur', () => clog.log('blur', { visible: document.visibilityState || null }));
  document.addEventListener('visibilitychange', () => clog.log('visibility', { state: document.visibilityState || null }));
  window.addEventListener('error', (ev) => clog.log('page.error', { message: ev.message || null, where: `${ev.filename || ''}:${ev.lineno || 0}` }));
  window.addEventListener('unhandledrejection', (ev) => clog.log('page.rejection', { message: String(ev.reason?.message || ev.reason || '') }));
  conn.api.bus?.addEventListener('maker-event', (ev) => { if (ev.detail?.type === 'maker.trace') { clog.setRemote(ev.detail.data?.on === true); clog.log('trace', { on: ev.detail.data?.on === true }); } });
  const actions = createActions({
    store, api: conn.api, mode: conn.mode, params, toast, reconnectMs: Number(params.get('reconnect')) || 2000, clog,
    mic: createCapture({ workletUrl: new URL('./audio/capture-worklet.js', import.meta.url).href }), player: createPlayer(), decodeFile,
    micIdleMs: Number(params.get('micidle')) || undefined, // test hook: a short idle time
    reasonText: (e) => ctx(store.get()).T(errorKey(e.code))
  });

  // ---------- fixtures: the states of the design brief that need a little set-up ----------
  async function applyFixture(name) {
    const patch = {};
    // the mockup's states show a set-up mic; the dev backend fakes that here (a real page starts with the mic not set up)
    if (conn.mode === 'dev' && name !== 'nomic' && name !== 'nodubs' && name !== 'micoff' && !params.has('realmic')) Object.assign(patch, { mic: 'ready', devices: DEVICES, device: DEVICES[1], level: 'good', lvl: 0.55 });
    if (name === 'countdown') Object.assign(patch, { rec: 'countdown', count: 2 });
    if (name === 'recording') { const r = mulberry(5); Object.assign(patch, { rec: 'recording', recElapsed: 2.6, lvl: 0.62, livePeaks: Array.from({ length: 96 }, (_, i) => (i < 60 ? 0.1 + r() * 0.7 * Math.sin(i / 9) ** 2 : 0.02)) }); }
    if (name === "nomic") patch.mic = "none";
    if (name === 'consent') patch.menu = 'consent';
    if (name === 'spoken') store.set({ last: { id: 'smalltalk_tea_002', variant: 'orig', at: Date.now() - 12000 } }); // the game just said a line far from the open one
    if (name === 'langmismatch') store.set({ status: { ...(store.get().status || {}), gameLanguage: 'en' } }); // the game runs in English, the pack is Thai
    if (name === 'micoff') Object.assign(patch, { mic: 'off', devices: DEVICES, device: DEVICES[1] });
    if (name === "unfocused" && conn.mode === "dev") conn.api.raw.devControls.setStatus({ focused: false, paused: false });
    if (name === 'picker') patch.menu = 'pack';
    if (name === 'keys') patch.menu = 'keys';
    if (name === 'lines') patch.page = 'lines';
    if (name === 'todo') { patch.page = 'lines'; patch.view = { ...store.get().view, groupByStatus: true }; }
    if (name === 'settings') await actions.openModal('settings');
    if (name === 'packs') await actions.openModal('packs');
    if (name === 'newpack') await actions.openModal('newpack');
    if (name === 'export') await actions.openModal('export');
    const urlPage = params.get('page');
    if (['packs', 'export', 'settings'].includes(urlPage) && !['packs', 'newpack', 'export', 'settings'].includes(name)) {
      await actions.openModal(urlPage);
    }
    if (name === 'qa') await actions.selectLine('pomodoro_break_003');
    if (name === 'otherpack') await actions.selectLine('night_goodnight_002'); // another pack provides the subtitle and the dub (dev backend)
    if (name === 'history') { await actions.selectLine('clickheroine_rest_010'); await actions.openHistory(); patch.menu = 'hist'; }
    if (name === 'tour') { patch.tour = { page: patch.page || store.get().page || 'studio', step: Number(params.get('step')) || 0 }; }
    if (name === 'conflict' && conn.mode === 'dev') {
      // another writer saves behind our back, then we type: the next save is a stale write
      const s = store.get(), line = await conn.api.raw.studioLine(s.packId, s.packLang, s.lineId);
      await conn.api.raw.studioSaveSubtitle(s.packId, s.packLang, s.lineId, { expectedRevision: line.revision, operationId: `${'0'.repeat(31)}1`, text: 'ฉบับจากแท็บอื่น' });
      actions.editText('ฉบับของฉันที่พิมพ์ค้างไว้');
      await actions.settle();
      await new Promise((r) => setTimeout(r, 200));
    }
    store.set(patch);
    if (name === 'offline' && conn.mode === 'dev') { conn.api.raw.devControls.setOffline(true); await actions.refreshStatus(); }
  }

  const PRESET_CAPTIONS = { th: 'ไทย', vi: 'Tiếng Việt', id: 'Bahasa Indonesia', es: 'Español', fr: 'Français', de: 'Deutsch' };
  function snapshotNewPackForm(extra = {}) {
    const prev = store.get().newPackForm || {};
    const preset = $('#np-lang')?.value ?? prev.langPreset ?? 'th';
    const rawCode = ($('#np-lang-code')?.value ?? (preset !== 'custom' ? preset : prev.lang) ?? 'th').trim().toLowerCase();
    return {
      ...prev,
      name: $('#np-name')?.value ?? prev.name ?? '',
      id: $('#np-id')?.value ?? prev.id ?? '',
      idCustom: $('#np-id')?.dataset?.custom === '1',
      nameCustom: $('#np-name')?.dataset?.custom === '1',
      author: $('#np-author')?.value ?? prev.author ?? '',
      lang: rawCode,
      langPreset: preset,
      langName: $('#np-lang-name')?.value ?? prev.langName ?? '',
      langNameCustom: $('#np-lang-name')?.dataset?.custom === '1',
      description: $('#np-desc')?.value ?? prev.description ?? '',
      ...extra
    };
  }
  function updatePackSuggestions() {
    const authorVal = $('#np-author')?.value ?? '';
    const preset = $('#np-lang')?.value;
    const langCode = ($('#np-lang-code')?.value || (preset && preset !== 'custom' ? preset : 'th')).trim().toLowerCase();
    const langCustomName = preset === 'custom' ? ($('#np-lang-name')?.value || '') : '';
    const nameIn = $('#np-name');
    const idIn = $('#np-id');
    const s = store.get();
    const T = makeT(effectiveLang(s.status?.gameLanguage, s.uiManual));
    if (nameIn && nameIn.dataset.custom !== '1') {
      const suggested = suggestPackName({ author: authorVal, langCode, langName: langCustomName, T });
      nameIn.value = suggested;
      nameIn.placeholder = suggested || T('newPackDefault');
    }
    if (idIn && idIn.dataset.custom !== '1') {
      const suggested = suggestPackId(authorVal, langCode, s.packs);
      idIn.value = suggested;
      idIn.placeholder = suggested || 'my-new-pack';
    }
  }
  function toPngDataUrl(bytes) {
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return `data:image/png;base64,${btoa(bin)}`;
  }

  // ---------- events ----------
  let closingMenuTimer = null;
  function cancelMenuClose() {
    if (closingMenuTimer) { clearTimeout(closingMenuTimer); closingMenuTimer = null; }
  }
  function closeActiveMenu(extraPatch = {}) {
    if (store.get().menu === 'keys') helpReturnFocus = true;
    cancelMenuClose();
    const activeMenus = [...document.querySelectorAll('.menu[data-motion="pop"]:not(.c-dd-list):not(.tour-card):not([hidden])')];
    const ms = motionSmallMs();
    if (!activeMenus.length || ms <= 0) {
      activeMenus.forEach((m) => closePop(m));
      store.set({ menu: null, ...extraPatch });
      return;
    }
    activeMenus.forEach((m) => closePop(m));
    closingMenuTimer = setTimeout(() => {
      closingMenuTimer = null;
      store.set({ menu: null, ...extraPatch });
    }, ms);
  }
  function closeTourWithMotion() {
    const tok = ++closingTourToken;
    const tc = el('tour-card');
    const cur = el('tour-cursor');
    const ms = motionSmallMs();
    const restoreHelpFocus = () => {
      const ae = document.activeElement;
      if (!ae || ae === document.body || ae === document.documentElement || el('tour-layer')?.contains(ae)) {
        document.querySelector('[data-act="keys-menu"]')?.focus({ preventScroll: true });
      }
    };
    if (!tc || ms <= 0) {
      if (tc) closePop(tc);
      store.set({ tour: null });
      restoreHelpFocus();
      return;
    }
    restoreHelpFocus();
    cur?.animate?.([{ opacity: 1 }, { opacity: 0 }], { duration: ms, easing: 'ease-out', fill: 'forwards' });
    closePop(tc, () => {
      if (tok === closingTourToken) store.set({ tour: null });
    });
  }
  // The companion tracks resizes itself while it is mounted (tour.js ensureTrackingListeners); no second listener here.
  bindDropdownEvents(() => {
    if (store.get().menu && !store.get().menu.startsWith('newpack')) {
      // Close the menu now, not after its exit animation: the delayed close rebuilds the top bar and the deck
      // about 250 ms later, which would destroy the dropdown that is about to open.
      cancelMenuClose();
      document.querySelectorAll('.menu[data-motion="pop"]:not(.c-dd-list):not(.tour-card):not([hidden])').forEach((m) => closePop(m));
      store.set({ menu: null });
      paint();
    }
  });

  document.addEventListener('click', async (ev) => {
    const target = ev.target.closest('[data-act]');
    const s = store.get();
    if (!target) {
      if (s.modal && ev.target.id === 'modal') { actions.closeModal(); return; }
      if (s.menu && !ev.target.closest('.menu, .picker, .hist-w, .settings-w, .keys-w, .ck, .c-dd')) closeActiveMenu();
      return;
    }
    const act = target.dataset.act, id = target.dataset.id;
    const curPage = s.page || 'studio';
    if (act === 'open-packs' || act === 'open-export' || ((act === 'new-pack' || act === 'new-pack-open') && curPage !== 'packs')) {
      const tab = act === 'open-export' ? 'export' : act === 'open-packs' ? 'packs' : 'newpack';
      const nextPage = tab === 'export' ? 'export' : 'packs';
      if (act === 'open-export' && id && id !== s.packId) {
        const p = s.packs.find((x) => x.id === id);
        if (p && p.selectable) { actions.flushCurrent(); await actions.loadPack(p.id, p.languages[0] || 'th'); }
      }
      let loadPromise = null;
      await transitionPage(store.get().page || 'studio', nextPage, () => {
        actions.openModalSync(tab);
        loadPromise = actions.loadModalData(tab);
        paintQueued = false; paint();
      });
      if (loadPromise) await loadPromise;
      return;
    }
    if (act === 'pick-pack' && curPage !== 'studio') {
      const p = s.packs.find((x) => x.id === id);
      if (p) {
        let loadPromise = null;
        await transitionPage(curPage, 'studio', () => {
          store.set({ modal: null, page: 'studio', packsSub: 'list', menu: null });
          actions.flushCurrent();
          loadPromise = actions.loadPack(p.id, p.languages[0] || 'th');
          paintQueued = false; paint();
        });
        if (loadPromise) await loadPromise;
      }
      return;
    }
    switch (act) {
      case 'ui-th': case 'ui-en': storeChoice(act.slice(3)); store.set({ uiManual: act.slice(3) }); break;
      case 'page-tab': {
        const nextPage = id || 'studio';
        const keepHelp = s.menu === 'keys';
        let loadPromise = null;
        await transitionPage(curPage, nextPage, () => {
          if (id === 'packs' || id === 'export' || id === 'settings') {
            actions.openModalSync(id);
            loadPromise = actions.loadModalData(id);
          } else {
            store.set({ page: nextPage, modal: null, menu: null });
          }
          if (keepHelp) store.set({ menu: 'keys' });
          paintQueued = false; paint();
        });
        if (loadPromise) await loadPromise;
        break;
      }
      case 'packs-sub': store.set({ page: 'packs', packsSub: id || 'list', modal: id === 'newpack' ? 'newpack' : 'packs' }); break;
      case 'keys-menu': cancelMenuClose(); closeAllDropdowns(); if (s.menu === 'keys') closeActiveMenu(); else store.set({ menu: 'keys', helpTab: 'guide', guideHint: false }); break;
      case 'help-close': closeActiveMenu(); break;
      case 'help-tab': store.set({ helpTab: id === 'glossary' ? 'glossary' : 'guide' }); break;
      case 'pack-menu': cancelMenuClose(); closeAllDropdowns(); if (s.menu === 'pack') closeActiveMenu(); else store.set({ menu: 'pack' }); break;
      case 'tour-start': {
        ++closingTourToken;
        teardownTourCompanion();
        cancelMenuClose();
        closeAllDropdowns();
        if (s.menu !== 'keys') break;
        const curPage = helpPage(s);
        markPageTourSeen(curPage);
        store.set({ menu: null, tour: { page: curPage, step: 0 } });
        break;
      }
      case 'tour-next': {
        if (!s.tour) break;
        const steps = tourStepsFor(s.tour.page || s.page || 'studio');
        if (s.tour.step + 1 < steps.length) store.set({ tour: { ...s.tour, step: s.tour.step + 1 } });
        else closeTourWithMotion();
        break;
      }
      case 'tour-prev': {
        if (s.tour && s.tour.step > 0) store.set({ tour: { ...s.tour, step: s.tour.step - 1 } });
        break;
      }
      case 'tour-skip': closeTourWithMotion(); break;
      case 'reset-tours': {
        ++closingTourToken;
        teardownTourCompanion();
        toast('t_tours_reset');
        store.set({ tour: null, menu: 'keys', helpTab: 'guide', guideHint: false });
        break;
      }
      case 'prev-scene':
      case 'next-scene': {
        const dGroups = displayGroups(s);
        if (dGroups.length) {
          const curId = currentSceneId(s);
          const idx = Math.max(0, dGroups.findIndex((g) => g.id === curId));
          const step = act === 'prev-scene' ? -1 : 1;
          const nextG = dGroups[(idx + step + dGroups.length) % dGroups.length];
          if (nextG) {
            store.set({ view: { ...s.view, group: nextG.id } });
            const firstL = s.lines.find((l) => (nextG.rawSet ? nextG.rawSet.has(l.group) : l.group === nextG.id));
            if (firstL) await actions.selectLine(firstL.id, { scroll: true });
          }
        }
        break;
      }
      case 'vb-mode': store.set({ view: { ...s.view, groupByStatus: id === 'status' } }); break;
      case 'open-line-studio': store.set({ page: 'studio', modal: null }); await actions.selectLine(id, { scroll: true }); break;
      case 'settings-menu': await actions.openModal('settings'); break;
      case 'pack-voice-origin': actions.setPackVoiceDraft(id); break;
      case 'pack-voice-save': await actions.savePackVoice(s.packVoiceDraft || s.voiceDefault?.origin || 'own', true, { packId: target.dataset.pack || s.packId }); break;
      case 'pack-voice-clear': await actions.clearPackVoice(target.dataset.pack || s.packId); break;
      case 'apply-pack-voice': await actions.applyPackVoiceToUnconfirmed(target.dataset.pack || s.packId); break;
      case 'take-override': actions.overrideTake(id); break;
      case 'take-follow-pack': await actions.followPackDefaultTake(id); break;
      case 'open-packs': await actions.openModal('packs'); break;
      case 'open-export': await actions.openModal('export', id || null); break;
      case 'new-pack': case 'new-pack-open': {
        const keepHelp = s.menu === 'keys';
        actions.openModalSync('newpack');
        if (keepHelp) store.set({ menu: 'keys' });
        await actions.loadModalData('newpack');
        break;
      }
      case 'modal-tab': await actions.openModal(id); break;
      case 'modal-close': actions.closeModal(); break;
      case 'np-cancel': store.set({ page: 'packs', packsSub: 'list', modal: 'packs' }); break;
      case 'np-create': {
        if (s.creatingPack) break;
        const idIn = $('#np-id');
        const customId = idIn?.value?.trim() || undefined;
        const preset = $('#np-lang')?.value;
        if (preset === 'custom' && !$('#np-lang-code')?.value?.trim()) { toast('np_lang_bad', 'warn'); break; }
        const rawCode = ($('#np-lang-code')?.value || (preset && preset !== 'custom' ? preset : 'th')).trim();
        store.set({ newPackForm: snapshotNewPackForm() });
        await actions.createPack({
          name: $('#np-name')?.value,
          id: customId,
          idCustom: idIn?.dataset.custom === '1',
          author: $('#np-author')?.value,
          description: $('#np-desc')?.value,
          lang: rawCode,
          langName: $('#np-lang-name')?.value || '',
          iconBytes: store.get().newPackForm?.iconBytes || null
        });
        break;
      }
      case 'np-pick-icon': store.set({ iconTargetPackId: '__new__' }); $('#icon-file')?.click(); break;
      case 'np-gen-icon': {
        const png = makeDefaultIconPng((Date.now() + 1) & 3);
        store.set({ newPackForm: snapshotNewPackForm({ iconBytes: png, iconPreviewUrl: toPngDataUrl(png), iconName: 'dusk-icon-64.png' }) });
        break;
      }
      case 'pack-icon-pick': store.set({ iconTargetPackId: id || s.packId }); $('#icon-file')?.click(); break;
      case 'export-check': await actions.runExportCheck(); break;
      case 'label-dubs': await actions.labelUnlabelledDubs(target.dataset.pack || s.packId); break;
      case 'export-bump': store.set({ exportBump: id, exportNotes: $('#exp-notes')?.value ?? s.exportNotes }); break;
      case 'export-run': await actions.runExport({ bump: s.exportBump || 'patch', notes: $('#exp-notes')?.value ?? s.exportNotes }); break;
      case 'export-download': await actions.downloadExportZip(target.dataset.pack || s.packId, target.dataset.file); break;
      case 'open-said': await actions.openSpoken(id); break;
      case 'match-lang': actions.flushCurrent(); await actions.loadPack(s.packId, id, { keepLine: true }); break;
      case 'pick-pack': { const p = s.packs.find((x) => x.id === id); if (p) { store.set({ modal: null, page: 'studio', packsSub: 'list', menu: null }); actions.flushCurrent(); await actions.loadPack(p.id, p.languages[0] || 'th'); } break; }
      case 'scene': store.set({ view: { ...s.view, group: id || null } }); break;
      case 'filter': {
        if (id === 'all') store.set({ view: { ...s.view, filter: 'all' } });
        else store.set({ view: { ...s.view, filter: id } });
        break;
      }
      case 'vfilter': {
        const nextV = { ...s.view };
        if (id && id !== 'all') nextV.voice = id; else delete nextV.voice;
        store.set({ view: nextV });
        break;
      }
      case 'next-unrecorded': store.set({ page: 'studio', modal: null }); await actions.nextUnrecorded(); break;
      case 'quick-record': await actions.quickRecord(); break;
      case 'keep-next': await actions.keepAndNext(); break;
      case 'retry-lines': await actions.retryLines(); break;
      case 'line': await actions.selectLine(id, { scroll: true }); break;
      case 'hist': { cancelMenuClose(); closeAllDropdowns(); const open = s.menu === 'hist'; if (open) closeActiveMenu(); else { store.set({ menu: 'hist' }); await actions.openHistory(); } break; }
      case 'revert': cancelMenuClose(); store.set({ menu: null }); await actions.revert(id); break;
      case 'status': actions.setStatus(id); break;
      case 'star-line': actions.toggleStar(); break;
      case 'retake': actions.toggleRetake(); break;
      case 'conflict-mine': actions.resolveConflict('mine'); break;
      case 'conflict-theirs': actions.resolveConflict('theirs'); break;
      case 'cps-src': store.set({ cpsAgainst: s.cpsAgainst === 'orig' ? 'auto' : 'orig' }); break;
      case 'take': actions.selectTake(id); break;
      case 'qa-fix': { const ed = $('#ed'); if (ed && !ed.readOnly) { ed.value = applyFix(ed.value, { k: target.dataset.k, x: target.dataset.x }); onEdit(ed.value); } break; }
      case 'gloss': { const ed = $('#ed'); if (ed && !ed.readOnly) { const r = insertTerm(ed.value, target.dataset.t, ed.selectionStart ?? ed.value.length); ed.value = r.text; ed.focus(); ed.setSelectionRange(r.caret, r.caret); onEdit(r.text); } break; }
      case 'setup-mic': await actions.setupMic(); break;
      case 'record': await actions.toggleRecord(); break;
      case 'import': $('#file')?.click(); break;
      case 'take-del': await actions.removeTake(id); break;
      case 'fav': await actions.toggleFavourite(id); break;
      case 'origin': if (target.closest('.consent-menu')) await actions.setOrigin(id); else actions.chooseTakeOrigin(id); break;
      case 'take-origin-save': await actions.saveTakeOrigin(); break;
      case 'consent-confirm': cancelMenuClose(); if (takeOriginDraft(store.get())) { actions.confirmTakeOrigin(true); store.set({ menu: null }); } else await actions.setConsent(true); break;
      case 'consent-cancel': closeActiveMenu({ pendingKeepNext: false }); break;
      case 'play-take': await actions.playTake(); break;
      case 'play-orig': await actions.playOriginal(); break;
      case 'play-mine': await actions.playMine(); break;
      case 'unpublish': await actions.unpublish(); break;
      case 'motion-mode': {
        const motion = setMotionSettings({ mode: id });
        store.set({ motion });
        syncParallaxToPage(store.get().page || 'studio');
        break;
      }
      default: break;
    }
  });
  let editSnapshot = null;
  function subtitleLimitHint(show) {
    const hint = el('ed-limit');
    if (hint) hint.textContent = show ? ctx(store.get()).T('subtitleLimit') : '';
  }
  function onEdit(text) {
    const s = store.get(), ed = $('#ed'), previous = editorText(s);
    if (!acceptsSubtitleEdit(text, previous, s.packLang)) {
      if (ed) {
        ed.value = previous;
        const pos = editSnapshot?.editor === ed ? editSnapshot : { start: Math.min(ed.selectionStart, previous.length), end: Math.min(ed.selectionEnd, previous.length) };
        ed.setSelectionRange(pos.start, pos.end);
      }
      subtitleLimitHint(true);
    } else {
      subtitleLimitHint(false);
      actions.editText(text);
    }
    editSnapshot = null;
    autosize(ed);
  }
  document.addEventListener('beforeinput', (ev) => {
    const ed = ev.target;
    if (ed.id !== 'ed' || ed.readOnly) return;
    editSnapshot = { editor: ed, start: ed.selectionStart, end: ed.selectionEnd };
    if (!ev.cancelable || !ev.inputType.startsWith('insert')) return;
    const added = ['insertLineBreak', 'insertParagraph'].includes(ev.inputType) ? '\n' : ev.data ?? ev.dataTransfer?.getData('text/plain');
    if (added == null) return; // Noncancelable IME/paste paths are checked again on input.
    const text = ed.value.slice(0, ed.selectionStart) + added + ed.value.slice(ed.selectionEnd);
    if (!acceptsSubtitleEdit(text, editorText(store.get()), store.get().packLang)) { ev.preventDefault(); subtitleLimitHint(true); }
  });
  document.addEventListener('input', (ev) => {
    const t = ev.target;
    if (t.id === 'ed') onEdit(t.value);
    else if (t.dataset?.in === 'q') store.set({ view: { ...store.get().view, q: t.value } });
    else if (t.id === 'np-name') {
      t.dataset.custom = t.value.trim() ? '1' : '';
    } else if (t.id === 'np-id') {
      t.dataset.custom = t.value.trim() ? '1' : '';
    } else if (t.id === 'np-author') {
      updatePackSuggestions();
    } else if (t.id === 'np-lang-code') {
      const code = t.value.trim().toLowerCase();
      const sel = $('#np-lang');
      if (sel) { sel.value = PRESET_CAPTIONS[code] ? code : 'custom'; syncDropdownFromSelect(sel); }
      const nameIn = $('#np-lang-name');
      if (nameIn && PRESET_CAPTIONS[code] && !nameIn.dataset.custom) nameIn.value = PRESET_CAPTIONS[code];
      updatePackSuggestions();
    } else if (t.id === 'np-lang-name') {
      t.dataset.custom = t.value.trim() ? '1' : '';
      updatePackSuggestions();
    } else if (t.id === 'exp-notes') {
      store.get().exportNotes = t.value;
    } else if (t.dataset?.in === 'motion-page' || t.dataset?.in === 'motion-small') {
      const isPage = t.dataset.in === 'motion-page';
      const motion = setMotionSettings(isPage ? { pageMs: Number(t.value) } : { smallMs: Number(t.value) });
      store.get().motion = motion;
      const out = t.closest('.st-motion-sl')?.querySelector('.st-motion-val');
      if (out) out.textContent = ctx(store.get()).T('motion_ms', { n: isPage ? motion.pageMs : motion.smallMs });
    }
    if (t.id?.startsWith('np-')) store.get().newPackForm = snapshotNewPackForm();
  });
  document.addEventListener('focusout', (ev) => { if (ev.target.id === 'ed') actions.flushCurrent(); });
  document.addEventListener('change', async (ev) => {
    const t = ev.target;
    if (t?.tagName === 'SELECT') syncDropdownFromSelect(t);
    if (t.id === 'np-lang') {
      const codeIn = $('#np-lang-code'), nameIn = $('#np-lang-name');
      if (t.value === 'custom') {
        if (codeIn) { codeIn.value = ''; codeIn.focus(); }
        if (nameIn) { nameIn.value = ''; nameIn.dataset.custom = ''; }
      } else {
        if (codeIn) codeIn.value = t.value;
        if (nameIn) { nameIn.value = PRESET_CAPTIONS[t.value] || ''; nameIn.dataset.custom = ''; }
      }
      updatePackSuggestions();
      store.get().newPackForm = snapshotNewPackForm();
      return;
    }
    if (t.id === 'file') { const f = t.files && t.files[0]; t.value = ''; await actions.importFile(f); return; }
    if (t.id === 'icon-file') {
      const f = t.files && t.files[0]; t.value = '';
      if (!f) return;
      const bytes = new Uint8Array(await f.arrayBuffer());
      const targetPack = store.get().iconTargetPackId;
      if (targetPack === '__new__') {
        store.set({ newPackForm: snapshotNewPackForm({ iconBytes: bytes, iconPreviewUrl: toPngDataUrl(bytes), iconName: f.name || 'icon.png' }) });
      } else if (targetPack) {
        await actions.uploadPackIcon(targetPack, bytes);
      }
      return;
    }
    if (t.dataset?.ch === 'scnfilter') {
      store.set({ view: { ...store.get().view, group: t.value || null } });
      return;
    }
    if (t.dataset?.ch === 'charfilter') {
      const s = store.get(), nextV = { ...s.view };
      if (t.value && t.value !== 'all') nextV.char = t.value; else delete nextV.char;
      store.set({ view: nextV });
      return;
    }
    if (t.dataset?.ch === 'even') { await actions.setEven(t.checked); return; }
    if (t.dataset?.ch === 'device') { await actions.selectDevice(t.value); return; }
    if (t.dataset?.ch === 'take-origin-consent') {
      if (t.checked) { t.checked = false; store.set({ menu: 'consent' }); } else actions.confirmTakeOrigin(false);
      return;
    }
    if (t.dataset?.ch === 'consent') {
      // confirming means reading the statement first: the tick opens it, the button inside confirms; un-ticking is immediate
      if (t.checked) { t.checked = false; store.set({ menu: 'consent' }); } else await actions.setConsent(false);
      return;
    }
    if (t.dataset?.ch === 'set-auto-done') { actions.setSetting('autoDoneOnNext', t.checked); return; }
    if (t.dataset?.ch === 'set-jump-done') { actions.setSetting('jumpAfterDone', t.checked); return; }
    if (t.dataset?.in === 'motion-page' || t.dataset?.in === 'motion-small') {
      store.set({ motion: setMotionSettings(t.dataset.in === 'motion-page' ? { pageMs: Number(t.value) } : { smallMs: Number(t.value) }) });
      return;
    }
    if (t.dataset?.ch === 'packlang') { const s = store.get(); actions.flushCurrent(); await actions.loadPack(s.packId, t.value, { keepLine: true }); }
  });
  document.addEventListener('keydown', (ev) => {
    if (ev.isComposing || ev.keyCode === 229) return;
    const inEditor = ev.target.id === 'ed', typing = ev.target.matches?.('input, textarea, select');
    const s = store.get();
    if (ev.key === 'Escape') {
      if (s.tour) closeTourWithMotion();
      else if (s.rec !== 'idle') actions.cancelRecord();
      else if (s.menu) closeActiveMenu();
      else if (s.modal) actions.closeModal();
      else if (s.playingTake) actions.stopTake();
      else if (inEditor) ev.target.blur();
      return;
    }
    if (s.menu === 'keys' && ev.target.matches?.('[data-help-tab]') && ['ArrowLeft','ArrowRight','Home','End'].includes(ev.key)) {
      ev.preventDefault();
      const id = ev.key === 'Home' ? 'guide' : ev.key === 'End' ? 'glossary' : s.helpTab === 'guide' ? 'glossary' : 'guide';
      store.set({ helpTab: id });
      paintQueued = false; paint();
      document.querySelector(`[data-help-tab="${id}"]`)?.focus({ preventScroll: true });
      return;
    }
    const guideDialog = s.tour ? el('tour-card') : s.menu === 'keys' ? document.querySelector('.help-menu') : null;
    if (guideDialog) {
      if (ev.key === 'Tab') {
        const controls = [...guideDialog.querySelectorAll('button:not(:disabled):not([tabindex="-1"]), a, [tabindex="0"]')];
        const first = controls[0], last = controls.at(-1);
        if (!guideDialog.contains(document.activeElement) || (ev.shiftKey && document.activeElement === first)) { ev.preventDefault(); last?.focus(); }
        else if (!ev.shiftKey && document.activeElement === last) { ev.preventDefault(); first?.focus(); }
      }
      return;
    }
    const handle = ev.target.closest?.('.handle');
    if (handle) {
      if ((s.page || 'studio') !== 'studio' || !canDub(s)) return;
      const take = currentTake(s), total = take?.sourceDurationSec || 0;
      if (!take || total < 0.1) return;
      const draft = s.trimDraft?.takeId === take.id ? s.trimDraft : null;
      let start = draft?.start ?? take.trimStartSec ?? 0, end = draft?.end ?? take.trimEndSec ?? total;
      const isStart = handle.dataset.h === 's';
      const min = isStart ? 0 : Math.ceil((start + 0.1 - 1e-9) * 1000) / 1000;
      const max = isStart ? Math.floor((end - 0.1 + 1e-9) * 1000) / 1000 : total;
      const value = isStart ? start : end;
      const delta = { ArrowLeft: -0.01, ArrowDown: -0.01, ArrowRight: 0.01, ArrowUp: 0.01, PageDown: -0.1, PageUp: 0.1 }[ev.key];
      if (delta === undefined && ev.key !== 'Home' && ev.key !== 'End') return;
      ev.preventDefault();
      const next = Math.max(min, Math.min(max, ev.key === 'Home' ? min : ev.key === 'End' ? max : value + delta));
      if (isStart) start = next; else end = next;
      actions.dragTrim(start, end);
      actions.commitTrim(start, end);
      return;
    }
    if (s.page === 'lines') {
      if (typing || s.modal || s.menu || !['ArrowDown', 'ArrowUp', 'Home', 'End', 'PageDown', 'PageUp'].includes(ev.key)) return;
      const list = el('vb-list'), lines = voiceLinesFor(s);
      if (!list || !lines.length) return;
      ev.preventDefault();
      const activeIndex = Number(document.activeElement?.dataset?.vbIndex);
      let index = Number.isInteger(activeIndex) ? activeIndex : -1;
      if (ev.key === 'Home') index = 0;
      else if (ev.key === 'End') index = lines.length - 1;
      else {
        const step = ev.key.startsWith('Page') ? Math.max(1, Math.floor(list.clientHeight / VB_ROW_PX)) : 1;
        index = Math.max(0, Math.min(lines.length - 1, index + (ev.key === 'ArrowUp' || ev.key === 'PageUp' ? -step : step)));
      }
      list.scrollTop = voiceRowTop(s, ctx(s), index);
      paintVoiceRows();
      list.querySelector(`[data-vb-index="${index}"]`)?.focus({ preventScroll: true });
      return;
    }
    if (s.page && s.page !== 'studio') return;
    if (typing && !inEditor) return;
    // R, Space, T, Shift+T and Enter work when the cursor is not in the box (and not on a button, where Space/Enter already mean "press")
    if (!inEditor && !ev.ctrlKey && !ev.metaKey && !ev.altKey) {
      const onButton = ev.target.closest?.('button, a');
      if (ev.key === 'r' || ev.key === 'R') { ev.preventDefault(); actions.toggleRecord(); return; }
      if (ev.key === ' ' && !onButton) { ev.preventDefault(); actions.playTake(); return; }
      if (ev.key === 't') { ev.preventDefault(); actions.playMine(); return; }
      if (ev.key === 'T') { ev.preventDefault(); actions.playOriginal(); return; }
      if (ev.key === 'Enter' && !onButton && !ev.shiftKey) { ev.preventDefault(); actions.completeLine(); return; }
    }
    if (ev.key === 'Enter' && (ev.metaKey || ev.ctrlKey)) {
      ev.preventDefault();
      actions.completeLine();
      return;
    }
    const move = (dir) => {
      const lines = sceneLinesOf(s), i = lines.findIndex((l) => l.id === s.lineId), next = lines[i + dir];
      if (next) actions.selectLine(next.id, { scroll: true });
    };
    if (inEditor && ev.key === 'Enter' && !ev.shiftKey) { ev.preventDefault(); move(1); return; }
    if (!typing && (ev.key === 'ArrowDown' || ev.key === 'ArrowUp')) { ev.preventDefault(); move(ev.key === 'ArrowDown' ? 1 : -1); }
  });
  // dropping an audio file on the deck imports it
  const deckEl = document.getElementById('deck');
  deckEl.addEventListener('dragover', (ev) => { if ([...(ev.dataTransfer?.types || [])].includes('Files')) { ev.preventDefault(); if (!store.get().dragOver) store.set({ dragOver: true }); } });
  deckEl.addEventListener('dragleave', (ev) => { if (!deckEl.contains(ev.relatedTarget)) store.set({ dragOver: false }); });
  deckEl.addEventListener('drop', async (ev) => { ev.preventDefault(); store.set({ dragOver: false }); await actions.importFile(ev.dataTransfer?.files?.[0]); });
  // trim handles: the screen follows the pointer, the loader hears about it when the handle is let go
  let dragging = null;
  deckEl.addEventListener('pointerdown', (ev) => {
    const h = ev.target.closest?.('.handle');
    if (!h || !canDub(store.get())) return;
    ev.preventDefault();
    const t = currentTake(store.get());
    if (!t || !t.sourceDurationSec) return;
    dragging = { which: h.dataset.h, total: t.sourceDurationSec, start: t.trimStartSec ?? 0, end: t.trimEndSec ?? t.sourceDurationSec };
    try { h.setPointerCapture?.(ev.pointerId); } catch { /* a synthetic pointer cannot be captured; the move events still arrive on the deck */ }
  });
  deckEl.addEventListener('pointermove', (ev) => {
    if (!dragging) return;
    const r = document.getElementById('wave').getBoundingClientRect(), x = Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width)) * dragging.total, gap = 0.1;
    if (dragging.which === 's') dragging.start = Math.min(x, dragging.end - gap); else dragging.end = Math.max(x, dragging.start + gap);
    actions.dragTrim(Math.max(0, dragging.start), Math.min(dragging.total, dragging.end));
  });
  const endDrag = async () => { if (!dragging) return; const d = dragging; dragging = null; await actions.commitTrim(Math.max(0, d.start), Math.min(d.total, d.end)); };
  deckEl.addEventListener('pointerup', endDrag);
  deckEl.addEventListener('pointercancel', endDrag);
  // anything typed and not yet sent goes out when the page is hidden or closed (best effort)
  addEventListener('pagehide', () => { actions.flushAll(); actions.release(); });
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') { actions.flushAll(); actions.releaseMic(); } });
  document.fonts?.ready.then(() => autosize($('#ed')));
  let resizeTimer = 0;
  addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(() => { memo.rows = null; paintRows(); paintVoiceRows(); autosize($('#ed')); renderSky(); syncParallaxToPage(store.get().page || 'studio'); }, 120); });

  try {
    await actions.start({ fixture: store.get().fixture });
    clog.log('ready', { lines: store.get().lines.length, linesError: store.get().linesError?.code ?? null });
  } catch (e) {
    clog.log('start.fail', { code: e?.code ?? null, status: e?.status ?? null, message: e?.message ?? null });
    // a dead connection is "offline" (the game may just not be running); only a loader that answers without Studio v2 is "too old"
    const offline = e instanceof StudioError && e.code === 'unavailable' && e.status === 0;
    if (offline) store.set({ ready: true, loadingLines: false, linesError: { code: e.code }, conn: { connected: false, unauthorized: false } });
    // a game that is still busy (it answers, but not yet with the packs) is not "too old": show the error with Retry
    const busy = e instanceof StudioError && ['unavailable', 'not_ready', 'internal', 'rate_limited', 'conflict'].includes(e.code);
    if (busy) store.set({ ready: true, loadingLines: false, linesError: { code: e.code } });
    if (!offline && !busy) { fatal('b_old_t'); return { store }; }
  }
  if (!store.get().linesError) await applyFixture(store.get().fixture);
  paint();
  await document.fonts.ready;
  paint(); renderSky(); syncParallaxToPage(store.get().page || 'studio');
  await new Promise((r) => requestAnimationFrame(() => r()));
  document.body.dataset.studio = 'ready';

  if (conn.mode === 'dev') globalThis.__studio = { store, conn, actions }; // test hook, dev backend only
  return { store, conn, actions };
}
