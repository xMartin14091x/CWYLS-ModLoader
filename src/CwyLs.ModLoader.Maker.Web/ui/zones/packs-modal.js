import { exportCheckDetail } from '../logic/export-copy.js';
import { MAX_BROWSER_ZIP, exportFolder } from '../api/model.js';
// Full-page views for Packs (cards/list of packs + New pack form), Export (checks, version bump, release notes, build + download zip),
// and Settings (pack voice origin + consent v2.4/v2.5/v2.5.1 + auto-status options).
import { esc, fmtN } from '../util.js';
import { I } from './icons.js';
import { packOf, canCreatePacks, canExport, canVoiceDefault, canLabelDubs, featureOn } from '../selectors.js';
import { langName } from './hero.js';
import { suggestPackName, suggestPackId } from '../logic/pack-naming.js';
import { renderDropdown } from './dropdown.js';
import { renderMotionSettings } from '../motion.js';

const ORIGINS = [['own', 'o_own'], ['ai-assisted', 'o_assist'], ['ai-generated', 'o_gen']];
const PRESET_LANGS = ['th', 'vi', 'id', 'es', 'fr', 'de'];
const DEFAULT_LANG_DISPLAY = {
  th: 'ไทย',
  vi: 'Tiếng Việt',
  id: 'Bahasa Indonesia',
  es: 'Español',
  fr: 'Français',
  de: 'Deutsch'
};

export function fmtBytes(n) {
  const b = Number(n) || 0;
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
  return `${(b / (1024 * 1024)).toFixed(2)} MB`;
}

function renderPackCard(s, p, T) {
  const info = s.packInfos?.[p.id] || null;
  const iconUrl = s.packIcons?.[p.id] || './ui/art/pack-maker-icon.svg';
  const cur = p.id === s.packId;
  const prog = cur ? s.overall : (s.packProgress?.[p.id] || null);
  const why = p.readOnlyKey ? T(p.readOnlyKey) : p.readOnlyText || T('ro_other');
  const statusBadge = p.restartRequired
    ? `<span class="pbadge pbadge-pend"><i class="ico ico-xs">${I.spark}</i>${esc(T('pending'))}</span>`
    : p.editable
      ? `<span class="pbadge pbadge-edit"><i class="ico ico-xs">${I.check}</i>${esc(T('packEditable'))}</span>`
      : `<span class="pbadge pbadge-ro"><i class="ico ico-xs">${I.lock}</i>${esc(why)}</span>`;
  const langs = (p.languages || []).map((l) => `<span class="plang-pill">${esc(langName(T, l))}</span>`).join('');
  const loadedBadge = p.loaded && !p.restartRequired && p.languages?.length
    ? `<span class="pbadge pbadge-ro pcard-loaded">${esc(T('packsLoaded', { lang: p.languages.map((l) => langName(T, l)).join(', ') }))}</span>`
    : '';
  const author = info?.author ? ` · ${esc(info.author)}` : '';
  const size = info?.sizeBytes ? ` · ${esc(fmtBytes(info.sizeBytes))}` : '';
  const desc = info?.description || (p.restartRequired ? T('ro_pending') : '');
  let progHtml = '';
  if (prog && prog.totalLines > 0) {
    const subPct = Math.min(100, (100 * (prog.subtitlePresent || 0)) / prog.totalLines).toFixed(1);
    const dubTot = prog.dubbableLines || 1;
    const dubPct = Math.min(100, (100 * (prog.dubPresent || 0)) / dubTot).toFixed(1);
    progHtml = `<div class="pcard-prog">` +
      `<div class="pcp-row"><span>${esc(T('subs'))} <b>${fmtN(prog.subtitlePresent)}</b>/${fmtN(prog.totalLines)}</span><i class="pcp-bar sub"><i style="width:${subPct}%"></i></i></div>` +
      `<div class="pcp-row"><span>${esc(T('dubs'))} <b>${fmtN(prog.dubPresent)}</b>/${fmtN(prog.dubbableLines)}</span><i class="pcp-bar dub"><i style="width:${dubPct}%"></i></i></div>` +
      `</div>`;
  }
  const canExpThis = p.editable && !p.restartRequired && featureOn(s, 'export');
  return `<article class="pcard panel-soft${cur ? ' cur' : ''}" data-pack-id="${esc(p.id)}">` +
    `<div class="pcard-top">` +
    `<img class="pcard-ico" src="${esc(iconUrl)}" alt="">` +
    `<div class="pcard-head">` +
    `<div class="pcard-title"><b>${esc(p.name)}</b>${cur ? `<span class="pbadge pbadge-cur">${esc(T('packsEditing'))}</span>` : ''}${statusBadge}${loadedBadge}</div>` +
    `<div class="pcard-meta"><code>${esc(p.id)}</code> · v${esc(info?.version || p.version || '0.1.0')}${author}${size} ${langs}</div>` +
    (desc ? `<div class="pcard-desc">${esc(desc)}</div>` : '') +
    `</div>` +
    `<div class="pcard-acts">` +
    `<button class="btn ${cur ? 'ghost on' : 'peach'} sm" data-act="pick-pack" data-id="${esc(p.id)}"${!p.selectable ? ' disabled' : ''}>${esc(cur ? T('packOpened') : T('openPack'))}</button>` +
    ((p.editable || p.restartRequired) ? `<button class="btn ghost sm" data-act="pack-icon-pick" data-id="${esc(p.id)}" title="${esc(T('changeIcon'))}"><i class="ico ico-xs">${I.image}</i><span>${esc(T('changeIcon'))}</span></button>` : '') +
    (canExpThis ? `<button class="btn ghost sm" data-act="open-export" data-id="${esc(p.id)}"><i class="ico ico-xs">${I.exportI}</i><span>${esc(T('exportBtn'))}</span></button>` : '') +
    `</div>` +
    `</div>` +
    progHtml +
    `</article>`;
}

function renderPacksTab(s, T) {
  const cards = s.packs.map((p) => renderPackCard(s, p, T)).join('');
  return `<div class="m-packs">` +
    `<div class="packs-wins-note panel-soft" role="note"><i class="ico ico-xs">${I.spark}</i><span>${esc(T('packsWinsInfo'))}</span></div>` +
    `<div class="pcard-list">${cards}</div>` +
    `</div>`;
}

export function renderNewPackTab(s, T) {
  const np = s.newPackForm || {};
  const iconUrl = np.iconPreviewUrl || './ui/art/pack-maker-icon.svg';
  const langCode = np.lang !== undefined ? np.lang : 'th';
  const presetVal = np.langPreset || (PRESET_LANGS.includes(langCode) ? langCode : 'custom');
  const langDisplay = np.langName !== undefined ? np.langName : (DEFAULT_LANG_DISPLAY[langCode] || '');
  const authorVal = np.author !== undefined ? np.author : (s.packInfos?.[s.packId]?.author || '');
  const customLangName = presetVal === 'custom' ? (np.langName || '') : '';
  const suggestedName = suggestPackName({ author: authorVal, langCode, langName: customLangName, T });
  const suggestedId = suggestPackId(authorVal, langCode, s.packs);
  const defaultName = suggestedName || T('newPackDefault');
  const defaultId = suggestedId || '';
  const nameVal = np.nameCustom ? (np.name ?? '') : (np.name !== undefined && np.name !== '' ? np.name : defaultName);
  const idVal = np.idCustom ? (np.id ?? '') : (np.id !== undefined && np.id !== '' ? np.id : defaultId);
  const namePlaceholder = suggestedName || T('newPackDefault');
  const idPlaceholder = suggestedId || 'my-new-pack';
  const npLangOpts = [
    ...PRESET_LANGS.map((lg) => ({ value: lg, label: `${T(`lang_${lg}`)} (${lg})` })),
    { value: 'custom', label: T('lang_custom') }
  ];
  return `<div class="m-newpack np panel-soft">` +
    `<div class="np-grid">` +
    `<div class="np-col">` +
    `<div class="np-row2">` +
    `<label class="np-f"><span>${esc(T('newPackName'))} *</span><input id="np-name" value="${esc(nameVal)}" maxlength="80" placeholder="${esc(namePlaceholder)}" data-custom="${np.nameCustom ? '1' : ''}"></label>` +
    `<label class="np-f"><span>${esc(T('newPackId'))} * <small>${esc(T('newPackIdHint'))}</small></span><input id="np-id" value="${esc(idVal)}" maxlength="64" placeholder="${esc(idPlaceholder)}" data-custom="${np.idCustom ? '1' : ''}"></label>` +
    `</div>` +
    `<div class="np-row3">` +
    `<div class="np-f"><span>${esc(T('newPackLang'))} *</span>${renderDropdown({ id: 'dd-np-lang', selectId: 'np-lang', value: presetVal, options: npLangOpts, ariaLabel: T('newPackLang'), className: 'dd-np-lang' })}</div>` +
    `<label class="np-f"><span>${esc(T('npLangCode'))} *</span><input id="np-lang-code" value="${esc(langCode)}" maxlength="12" placeholder="${esc(T('npLangCodePh'))}"></label>` +
    `<label class="np-f"><span>${esc(T('npLangDisplay'))}</span><input id="np-lang-name" value="${esc(langDisplay)}" maxlength="48" data-custom="${np.langNameCustom ? '1' : ''}" placeholder="${esc(T('npLangDisplayPh'))}"></label>` +
    `</div>` +
    `<p class="np-lang-why" role="note">${esc(T('npLangWhy'))}</p>` +
    `<div class="np-row2">` +
    `<label class="np-f"><span>${esc(T('newPackAuthor'))}</span><input id="np-author" value="${esc(authorVal)}" maxlength="80" placeholder="${esc(T('newPackAuthorPh'))}"></label>` +
    `<label class="np-f"><span>${esc(T('newPackDesc'))}</span><input id="np-desc" value="${esc(np.description ?? '')}" maxlength="500" placeholder="${esc(T('newPackDescPh'))}"></label>` +
    `</div>` +
    `</div>` +
    `<div class="np-side">` +
    `<div class="np-icon-box panel-soft">` +
    `<img class="np-icon-prev" id="np-icon-prev" src="${esc(iconUrl)}" alt="">` +
    `<div class="np-icon-info"><b>${esc(T('packIconLabel'))}</b><span>${esc(np.iconName ? np.iconName : T('iconRuleNote'))}</span>` +
    `<div class="np-icon-acts">` +
    `<button class="btn ghost sm" type="button" data-act="np-pick-icon"><i class="ico ico-xs">${I.image}</i><span>${esc(T('uploadIcon'))}</span></button>` +
    `<button class="btn ghost sm" type="button" data-act="np-gen-icon"><i class="ico ico-xs">${I.spark}</i><span>${esc(T('usePresetIcon'))}</span></button>` +
    `</div></div></div>` +
    `<div class="np-restart" role="note"><i class="ico ico-s">${I.spark}</i><div><b>${esc(T('npRestartTitle'))}</b><span>${esc(T('npRestartBody'))}</span></div></div>` +
    `</div>` +
    `</div>` +
    `<div class="np-b"><button class="btn ghost" data-act="np-cancel">${esc(T('cancel'))}</button><button class="btn peach" data-act="np-create"${canCreatePacks(s) && !s.creatingPack ? '' : ' disabled'}>${esc(T('create'))}</button></div>` +
    `</div>`;
}

function renderExportTab(s, T, lang) {
  const p = packOf(s);
  if (!p) return '';
  if (p.restartRequired || !p.editable) {
    const msg = p.restartRequired ? T('b_first_export') : T('b_ro');
    return `<div class="m-export"><div class="exp-empty panel-soft"><i class="ico">${I.lock}</i><div><b>${esc(p.name)}</b><p>${esc(msg)}</p></div></div></div>`;
  }
  const exp = s.exportPreview?.packId === p.id ? s.exportPreview : null;
  const res = s.exportResult?.packId === p.id ? s.exportResult : null;
  const bump = s.exportBump || 'patch';
  const notes = s.exportNotes ?? '';
  if (s.exportLoading && !exp) {
    return `<div class="m-export"><div class="exp-empty panel-soft"><span class="spin-dot"></span><span>${esc(T('expChecking'))}</span></div></div>`;
  }
  const ver = exp?.version || p.version || '0.1.0';
  const files = exp?.files || { included: 0, includedBytes: 0, excluded: 0, excludedBytes: 0 };
  const checks = exp?.checks || [];
  const checkRows = checks.map((c) => {
    const sevCls = c.severity === 'error' ? 'err' : c.severity === 'warning' ? 'warn' : 'ok';
    const sevTxt = T(`sev_${c.severity}`);
    const paths = c.paths?.length ? `<div class="chk-paths">${esc(c.paths.slice(0, 4).join(', '))}${c.paths.length > 4 ? ` (+${c.paths.length - 4})` : ''}</div>` : '';
    return `<li class="chk-row chk-${sevCls}">` +
      `<span class="chk-badge ${sevCls}">${esc(sevTxt)}</span>` +
      `<div class="chk-main"><b>${esc(T(`chk_${c.id}`))}</b> <span>${esc(exportCheckDetail(c, T, lang))}</span>${paths}</div>` +
      `</li>`;
  }).join('');

  // D-148: unlabelled dubs block the export; one click labels them all with the pack default (or opens Settings to set one first).
  const unlabelled = checks.find((c) => c.id === 'provenance' && c.code === 'provUnlabelled');
  let labelHtml = '';
  if (unlabelled && canLabelDubs(s)) {
    const vd = s.voiceDefault || s.packVoices?.[p.id] || null;
    const hasDef = Boolean(vd?.origin && vd?.consent?.confirmed);
    const originKey = { own: 'o_own', 'ai-assisted': 'o_assist', 'ai-generated': 'o_gen' }[vd?.origin];
    const n = unlabelled.params?.n ?? unlabelled.count ?? 0;
    const note = hasDef ? T('labelDubsNote', { n, origin: originKey ? T(originKey) : vd.origin }) : T('labelDubsNoDefault');
    labelHtml = `<div class="exp-label panel-soft" role="group">` +
      `<p class="exp-label-note">${esc(note)}</p>` +
      `<button class="btn peach sm" type="button" data-act="label-dubs" data-pack="${esc(p.id)}"${s.labelBusy ? ' disabled' : ''}>` +
      `${s.labelBusy ? '<span class="spin-dot"></span>' : `<i class="ico ico-xs">${I.check}</i>`}<span>${esc(T('labelDubsBtn'))}</span></button>` +
      `</div>`;
  }

  const bumps = ['patch', 'minor', 'major', 'none'].map((b) => {
    const targetVer = b === 'none' ? ver : (exp?.nextVersions?.[b] || ver);
    return `<button type="button" class="bump-b${bump === b ? ' on' : ''}" data-act="export-bump" data-id="${b}">` +
      `<span class="bump-k">${esc(T(`bump_${b}`))}</span><b>v${esc(targetVer)}</b></button>`;
  }).join('');

  const dl = res || (exp?.lastExport ? { ...exp.lastExport, note: '' } : null);
  // TD-5: name the folder the zip is saved in; a zip over the browser limit is explained there instead of a download that fails.
  const tooBig = dl && (dl.downloadable === false || dl.sizeBytes > MAX_BROWSER_ZIP);
  const where = dl ? exportFolder(dl, p.id) : '';
  const resHtml = dl ? `<div class="exp-res panel-soft">` +
    `<div class="exp-res-info"><i class="ico ico-s">${I.check}</i><div><b>${esc(dl.fileName)}</b>` +
    `<span>v${esc(dl.version)} · ${esc(fmtBytes(dl.sizeBytes))}${dl.sha256 ? ` · SHA-256 ${esc(dl.sha256.slice(0, 12))}…` : ''}${dl.note ? ` · ${esc(dl.note)}` : ''}</span>` +
    `<span class="exp-res-folder">${esc(tooBig ? T('e_zip_too_big', { folder: where }) : T('expSavedIn', { folder: where }))}</span></div></div>` +
    (tooBig ? '' : `<button class="btn peach sm" data-act="export-download" data-pack="${esc(p.id)}" data-file="${esc(dl.fileName)}"><i class="ico ico-xs">${I.download}</i><span>${esc(T('downloadZip'))}</span></button>`) +
    `</div>` : '';

  const summaryCls = exp?.errorCount > 0 ? 'err' : exp?.warningCount > 0 ? 'warn' : 'ok';
  const summaryTxt = !exp ? '' : exp.errorCount > 0 ? T('expBlocked', { n: exp.errorCount }) : exp.warningCount > 0 ? T('expReadyWarn', { n: exp.warningCount }) : T('expAllOk');

  return `<div class="m-export">` +
    `<div class="exp-top panel-soft">` +
    `<div class="exp-pack"><b>${esc(p.name)}</b> <span>v${esc(ver)} · ${esc(T('expFilesInfo', { inc: files.included, size: fmtBytes(files.includedBytes), exc: files.excluded }))}</span></div>` +
    `<button class="btn ghost sm" data-act="export-check"><i class="ico ico-xs">${I.spark}</i><span>${esc(T('expRecheck'))}</span></button>` +
    `</div>` +
    `<div class="exp-grid">` +
    `<div class="exp-left panel-soft">` +
    (summaryTxt ? `<div class="exp-sum exp-sum-${summaryCls}">${esc(summaryTxt)}</div>` : '') +
    `<ul class="chk-list">${checkRows}</ul>` +
    labelHtml +
    `</div>` +
    `<div class="exp-right panel-soft">` +
    `<div class="np-f"><span>${esc(T('expNextVer'))}</span><div class="bump-group" role="group">${bumps}</div></div>` +
    `<label class="np-f"><span>${esc(T('expNotesLabel'))} <small>${esc(T('expNotesHint'))}</small></span>` +
    `<textarea id="exp-notes" rows="3" maxlength="4000" placeholder="${esc(T('expNotesPh'))}">${esc(notes)}</textarea></label>` +
    `<div class="exp-act-row">` +
    `<button class="btn peach" data-act="export-run"${!exp || !exp.ok || s.exportBusy ? ' disabled' : ''}>` +
    `${s.exportBusy ? '<span class="spin-dot"></span>' : `<i class="ico ico-s">${I.exportI}</i>`}<span>${esc(T('expBuildBtn'))}</span></button>` +
    `</div>` +
    resHtml +
    `</div>` +
    `</div>` +
    `</div>`;
}

export function renderPacksPage(s, { T }) {
  const sub = s.packsSub === 'newpack' || s.modal === 'newpack' ? 'newpack' : 'list';
  return `<div class="pg-wrap m-packs-page">` +
    `<header class="pg-head panel">` +
    `<div class="pg-title"><b>${esc(sub === 'newpack' ? T('createPack') : T('packsView'))}</b><span>${esc(T('packsIntro'))}</span></div>` +
    `<div class="pg-subtabs" role="tablist">` +
    `<button type="button" class="btn ${sub !== 'newpack' ? 'peach' : 'ghost'} sm" data-act="packs-sub" data-id="list"><i class="ico ico-xs">${I.packs}</i><span>${esc(T('yourPacks'))}</span></button>` +
    `<button type="button" class="btn ${sub === 'newpack' ? 'peach' : 'ghost'} sm" data-act="new-pack"${canCreatePacks(s) ? '' : ' disabled'}><i class="ico ico-xs">${I.plus}</i><span>${esc(T('createPack'))}</span></button>` +
    `</div>` +
    `</header>` +
    `<div class="pg-body">${sub === 'newpack' ? renderNewPackTab(s, T) : renderPacksTab(s, T)}</div>` +
    `</div>`;
}

export function renderExportPage(s, { T, lang }) {
  return `<div class="pg-wrap m-export-page">` +
    `<header class="pg-head panel">` +
    `<div class="pg-title"><b>${esc(T('exportPack'))}</b><span>${esc(T('exportIntro'))}</span></div>` +
    `</header>` +
    `<div class="pg-body">${renderExportTab(s, T, lang)}</div>` +
    `</div>`;
}

export function renderSettingsPage(s, { T }) {
  const p = packOf(s);
  const canVd = canVoiceDefault(s);
  const vd = s.voiceDefault || (p ? s.packVoices?.[p.id] : null) || null;
  const hasDef = Boolean(vd?.origin && vd?.consent?.confirmed);
  const draftOrigin = s.packVoiceDraft || vd?.origin || 'own';
  const isSameConfirmed = hasDef && vd?.origin === draftOrigin;
  const unconf = s.unconfirmedCount || 0;
  const st = s.settings || { autoDoneOnNext: false, jumpAfterDone: true };

  return `<div class="pg-wrap m-settings-page">` +
    `<header class="pg-head panel">` +
    `<div class="pg-title"><b>${esc(T('settingsTitle'))}</b><span>${esc(p ? `${p.name} (${p.id})` : '')}</span></div>` +
    `</header>` +
    `<div class="pg-body m-settings">` +
    `<section class="st-voice panel-soft">` +
    `<div class="st-voice-h">` +
    `<div><b>${esc(T('packVoiceTitle'))}</b><p>${esc(T('consentPackOnce'))}</p></div>` +
    `<span class="pbadge ${hasDef ? 'pbadge-edit' : 'pbadge-ro'}">${esc(hasDef ? T('packVoiceConfirmed') : T('packVoiceUnset'))}</span>` +
    `</div>` +
    `<div class="origin st-origin" role="radiogroup" aria-label="${esc(T('origin'))}">` +
    ORIGINS.map(([o, key]) => `<button type="button" class="og${draftOrigin === o ? ' on' : ''}" role="radio" aria-checked="${draftOrigin === o}" data-act="pack-voice-origin" data-id="${o}"${canVd ? '' : ' disabled'}>${esc(T(key))}</button>`).join('') +
    `</div>` +
    `<div class="st-consent-box">${esc(T('consent_text'))}</div>` +
    `<div class="st-voice-acts">` +
    `<button type="button" class="btn ${isSameConfirmed ? 'ghost on' : 'peach'} sm" data-act="pack-voice-save" data-pack="${esc(p?.id || '')}"${canVd ? '' : ' disabled'}><i class="ico ico-xs">${I.check}</i><span>${esc(isSameConfirmed ? T('packVoiceSavedBtn') : T('packVoiceConfirmBtn'))}</span></button>` +
    (hasDef ? `<button type="button" class="btn ghost sm" data-act="pack-voice-clear" data-pack="${esc(p?.id || '')}"${canVd ? '' : ' disabled'}>${esc(T('packVoiceClear'))}</button>` : '') +
    `<button type="button" class="btn ghost sm pv-apply" data-act="apply-pack-voice" data-pack="${esc(p?.id || '')}"${!canVd || !hasDef || unconf === 0 ? ' disabled' : ''}>${esc(T('applyPackVoiceBtn', { n: unconf }))}</button>` +
    `</div>` +
    `</section>` +
    `<section class="st-workflow panel-soft">` +
    `<div class="st-wf-h"><b>${esc(T('settingsTitle'))}</b></div>` +
    `<label class="st-chk"><input type="checkbox" data-ch="set-auto-done"${st.autoDoneOnNext ? ' checked' : ''}><div><b>${esc(T('set_autoDone'))}</b><span>${esc(T('set_autoDoneHint'))}</span></div></label>` +
    `<label class="st-chk"><input type="checkbox" data-ch="set-jump-done"${st.jumpAfterDone !== false ? ' checked' : ''}><div><b>${esc(T('set_jumpDone'))}</b><span>${esc(T('set_jumpDoneHint'))}</span></div></label>` +
    `<div class="st-chk st-tours-row"><div class="st-tours-info"><b>${esc(T('set_toursTitle'))}</b><span>${esc(T('set_toursHint'))}</span></div><button type="button" class="btn ghost sm st-tours-btn" data-act="reset-tours"><i class="ico ico-xs">${I.spark}</i><span>${esc(T('set_showToursAgain'))}</span></button></div>` +
    renderMotionSettings(s, T) +
    `</section>` +
    `</div>` +
    `</div>`;
}

export function renderModal(s, ctx) {
  const tab = s.modal || (s.menu === 'newpack' ? 'newpack' : null);
  if (!tab) return '';
  if (tab === 'export') return renderExportPage(s, ctx);
  if (tab === 'settings') return renderSettingsPage(s, ctx);
  return renderPacksPage({ ...s, packsSub: tab === 'newpack' ? 'newpack' : 'list' }, ctx);
}
