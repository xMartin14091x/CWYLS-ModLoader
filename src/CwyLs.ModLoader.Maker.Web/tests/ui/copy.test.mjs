import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import en from '../../ui/i18n/en.js';
import th from '../../ui/i18n/th.js';
import { makeT, followGame, effectiveLang, storedChoice, storeChoice } from '../../ui/i18n/i18n.js';
import { CONSENT_VERSION } from '../../ui/logic/takes.js';

// The statement W2's gate approved as "consent-1" (research/2026-11-08/w2-backend/GATE-M1.md, answer 3). Kept as a literal here
// because the private research folder is not part of the repository.
const GATE_EN = "I confirm this take is my own voice, or I have the speaker's permission to use it in this pack, and the origin I picked is true. I will not present an AI voice as the original voice actor.";
const GATE_TH = 'ฉันยืนยันว่าเทคนี้เป็นเสียงของฉันเอง หรือได้รับอนุญาตจากเจ้าของเสียงให้ใช้ในแพ็กนี้ และแหล่งที่มาที่เลือกเป็นความจริง จะไม่นำเสียง AI ไปอ้างว่าเป็นเสียงของนักพากย์ต้นฉบับ';

const flatten = (o, prefix = '') => Object.entries(o).flatMap(([k, v]) =>
  Array.isArray(v) ? v.map((x, i) => [`${prefix}${k}[${i}]`, x]) : v && typeof v === 'object' ? flatten(v, `${prefix}${k}.`) : [[`${prefix}${k}`, v]]);
const placeholders = (s) => (String(s).match(/\{\w+\}/g) || []).sort().join(',');
const guidePath = (name) => new URL(`../../../../docs/guide/${name}`, import.meta.url);
const guideLabelKeys = [
  'tagline', 'tab_studio', 'tab_lines', 'tab_packs', 'tab_export', 'tab_settings',
  'createPack', 'newPackName', 'newPackLang', 'newPackId', 'newPackAuthor', 'newPackDesc',
  'npLangCode', 'npLangDisplay', 'packIconLabel', 'uploadIcon', 'usePresetIcon',
  'scenes', 'allScenes', 'search', 'cf_label', 'cf_all', 'cf_narrator', 'vf_label', 'vf_all',
  'vf_voiced', 'vf_unvoiced', 'f_all', 'f_todo', 'f_draft', 'f_done', 'f_retake', 'f_warn',
  'f_star', 'f_nosub', 'f_nodub', 'st_todo', 'st_draft', 'st_done', 'st_retake',
  'shiftEnter', 'doneKeyTip', 'set_autoDone', 'set_jumpDone', 'deck', 'setupMic', 'record',
  'import', 'keepNext', 'packVoiceTitle', 'origin', 'o_own', 'o_assist', 'o_gen',
  'packVoiceConfirmBtn', 'overrideTake', 'usePackDefault', 'consent', 'play_orig', 'play_mine',
  'chk_manifest', 'chk_subtitles', 'chk_audio', 'chk_provenance', 'chk_size', 'chk_progress',
  'chk_links', 'expNextVer', 'bump_patch', 'bump_minor', 'bump_major', 'bump_none',
  'expNotesLabel', 'expBuildBtn', 'downloadZip', 'helpTitle', 'helpHowTitle',
  'helpConnectTitle', 'keysTitle', 'tourStartBtn', 'set_showToursAgain', 'motion_title',
  'motion_mode', 'motion_off', 'motion_reduced', 'motion_full', 'motion_page', 'motion_small'
];

test('every English key exists in Thai and the other way round', () => {
  const a = flatten(en).map(([k]) => k).sort(), b = flatten(th).map(([k]) => k).sort();
  assert.deepEqual(a.filter((k) => !b.includes(k)), [], 'missing in th.js');
  assert.deepEqual(b.filter((k) => !a.includes(k)), [], 'missing in en.js');
});

test('no empty strings, and the same {placeholders} in both languages', () => {
  const t = Object.fromEntries(flatten(th));
  for (const [k, v] of flatten(en)) {
    assert.ok(String(v).trim().length > 0, `en ${k} is empty`);
    assert.ok(String(t[k]).trim().length > 0, `th ${k} is empty`);
    assert.equal(placeholders(t[k]), placeholders(v), `placeholders of ${k} differ`);
  }
});

test('creator guides keep their English and Thai labels aligned with the UI', async () => {
  const guides = {};
  for (const [lang, strings] of [['en', en], ['th', th]]) {
    const guideName = lang === 'en' ? 'pack-maker.md' : 'pack-maker.th.md';
    const guide = await readFile(guidePath(guideName), 'utf8');
    guides[lang] = guide;
    for (const key of guideLabelKeys) {
      assert.ok(guide.includes(strings[key]), `${lang} creator guide is missing ${key}: ${strings[key]}`);
    }
  }
  assert.match(guides.en, /\(pack-maker\.th\.md\)/);
  assert.match(guides.th, /\(pack-maker\.md\)/);
  const index = await readFile(guidePath('README.md'), 'utf8');
  assert.match(index, /\[Dub & Subtitle Studio\]\(pack-maker\.md\)/);
  assert.match(index, /\[Dub & Subtitle Studio \(ไทย\)\]\(pack-maker\.th\.md\)/);
  const [gettingStarted, textures] = await Promise.all([
    readFile(guidePath('getting-started.md'), 'utf8'),
    readFile(guidePath('textures.md'), 'utf8')
  ]);
  assert.doesNotMatch(gettingStarted, /planned for 1\.0/);
  assert.doesNotMatch(textures, /planned for 1\.0/);
});

test('copy rules: "Real-time" never "สด ๆ"; the product is "CWY:LS Mod Loader v<version>", never "CWY:LS 1.0"', () => {
  for (const [lang, strings] of [['en', en], ['th', th]]) {
    for (const [k, v] of flatten(strings)) {
      assert.ok(!/สด\s*ๆ/.test(String(v)), `${lang} ${k} says สด ๆ`);
      assert.ok(!/CWY:LS 1\.0/.test(String(v)), `${lang} ${k} says "CWY:LS 1.0"`);
    }
  }
  assert.match(en.tagline, /CWY:LS Mod Loader v\d+\.\d+(\.\d+)? /);
  assert.match(th.tagline, /CWY:LS Mod Loader v\d+\.\d+(\.\d+)? /);
  assert.match(th.npRestartBody, /Real-time/);
  assert.match(en.npRestartBody, /Real-time/);
});

test('Thai has Thai text where the UI speaks (not a copy of the English)', () => {
  const t = Object.fromEntries(flatten(th)), e = Object.fromEntries(flatten(en));
  const sameAsEnglish = Object.keys(e).filter((k) => typeof e[k] === 'string' && e[k] === t[k] && /[A-Za-z]{4,}/.test(e[k]) && !/^(tagline|consent_version)$/.test(k));
  assert.deepEqual(sameAsEnglish, [], 'untranslated Thai strings');
});

test('the consent statement and its version are exactly the approved text', () => {
  assert.equal(en.consent_text, GATE_EN);
  assert.equal(th.consent_text, GATE_TH);
  assert.equal(en.consent_version, CONSENT_VERSION);
  assert.equal(th.consent_version, CONSENT_VERSION);
});

test('every error code the model knows has a message in both languages', async () => {
  const codes = ['bad_request', 'unauthorized', 'read_only_pack', 'consent_required', 'not_found', 'conflict', 'take_limit', 'take_in_use', 'render_required',
    'restart_required', 'game_paused', 'not_ready', 'too_large', 'storage_limit', 'unsupported_media_type', 'unavailable', 'metadata_unavailable', 'recovery_required', 'rate_limited', 'internal'];
  for (const c of codes) { assert.ok(en[`e_${c}`], `en e_${c}`); assert.ok(th[`e_${c}`], `th e_${c}`); }
});

test('T falls back to English, then to the key, and fills placeholders', () => {
  const T = makeT('th');
  assert.equal(T('histMin', { n: 5 }), '5 นาทีก่อน');
  assert.equal(T('definitely_missing'), 'definitely_missing');
  assert.deepEqual(T('firstSteps').length, 3);
});

test('UI language follows the game language; a manual choice wins; unknown languages show English', () => {
  assert.equal(followGame('th'), 'th');
  assert.equal(followGame('en'), 'en');
  assert.equal(followGame('ja'), 'en');
  assert.equal(effectiveLang('th', null), 'th');
  assert.equal(effectiveLang('th', 'en'), 'en');
  assert.equal(effectiveLang(undefined, null), 'en');
});

test('the manual choice is the only thing stored, and a broken storage never throws', () => {
  const mem = new Map();
  const storage = { getItem: (k) => mem.get(k) ?? null, setItem: (k, v) => mem.set(k, v) };
  assert.equal(storedChoice(storage), null);
  storeChoice('en', storage);
  assert.equal(storedChoice(storage), 'en');
  mem.set('cwyls-maker-ui-lang', 'fr');
  assert.equal(storedChoice(storage), null, 'only th or en are accepted');
  const broken = { getItem() { throw new Error('denied'); }, setItem() { throw new Error('denied'); } };
  assert.equal(storedChoice(broken), null);
  assert.doesNotThrow(() => storeChoice('th', broken));
});
