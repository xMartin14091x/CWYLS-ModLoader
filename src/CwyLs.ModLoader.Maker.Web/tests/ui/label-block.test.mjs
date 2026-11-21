// D-148 (audit F5 + IG-04, T-111): an unlabelled dub blocks export; the export page offers one button that labels every
// unlabelled dub with the pack's voice default, or opens Settings to set that default first. Never labels silently.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { renderExportPage } from '../../ui/zones/packs-modal.js';
import { exportCheckDetail } from '../../ui/logic/export-copy.js';
import { STRINGS, makeT } from '../../ui/i18n/i18n.js';

const tick = () => new Promise((r) => setImmediate(r));
const drain = async () => { for (let i = 0; i < 20; i++) await tick(); };
const P = 'satone-th-dub';
const DUBS = ['audio/voice/th/Voice_Hand_001.wav', 'audio/voice/th/Voice_V1_002.wav'];

async function setup() {
  const dev = createDevBackend({ lines: 40 });
  const toasts = [];
  const store = createStore({ ready: false, conn: { connected: false, unauthorized: false }, packs: [], packId: null, packLang: 'th', groups: [], overall: {}, lines: [], byId: new Map(), view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, history: null, menu: null, selTakeId: null, peaks: {}, mic: 'none', rec: 'idle', trimDraft: null, playing: false, playingTake: false });
  let seq = 0;
  const api = createApi(dev);
  const actions = createActions({ store, api, mode: 'dev', timers: { setTimeout, clearTimeout }, now: () => Date.now(), selectDelayMs: 0, storage: null,
    newOp: () => (++seq).toString(16).padStart(32, '0'), toast: (key, kind, params) => toasts.push({ key, kind, params }) });
  await actions.start(); await drain();
  if (store.get().packId !== P) { await actions.loadPack(P, 'th'); await drain(); }
  dev.devUnlabelDubs(P, DUBS);
  return { dev, store, actions, toasts };
}

const page = (s, lang = 'en') => renderExportPage(s, { T: makeT(lang), lang });
const labelCalls = (dev) => (dev.dev?.log || dev.log).filter((x) => x.method === 'labelDubs');

test('an unlabelled dub is an export error, worded from its code in Thai and English, and Export stays disabled', async () => {
  const c = await setup();
  await c.actions.openModal('export'); await drain();
  const prov = c.store.get().exportPreview.checks.find((x) => x.id === 'provenance');
  assert.equal(prov.severity, 'error');
  assert.equal(prov.code, 'provUnlabelled');
  assert.equal(c.store.get().exportPreview.ok, false);
  for (const lang of ['en', 'th']) {
    const text = exportCheckDetail(prov, makeT(lang), lang);
    assert.equal(text, makeT(lang)('xc_provUnlabelled', { n: 2 }));
    assert.match(text, /2/);
  }
  assert.doesNotMatch(STRINGS.th.xc_provUnlabelled, /[A-Za-z]{3,}/, 'casual Thai, no English words');
  const html = page(c.store.get());
  assert.match(html, /data-act="export-run" disabled/);
  assert.ok(html.includes('Voice_Hand_001.wav'), 'the check names which dubs');
});

test('with a pack default the page shows one label button naming the default, in Thai and English', async () => {
  const c = await setup();
  await c.actions.savePackVoice('ai-assisted', true, { silent: true });
  await c.actions.openModal('export'); await drain();
  const s = c.store.get();
  const en = page(s, 'en'), th = page(s, 'th');
  assert.equal((en.match(/data-act="label-dubs"/g) || []).length, 1);
  assert.ok(en.includes(STRINGS.en.labelDubsBtn));
  assert.ok(en.includes(makeT('en')('labelDubsNote', { n: 2, origin: STRINGS.en.o_assist })));
  assert.ok(th.includes(STRINGS.th.labelDubsBtn));
  assert.ok(th.includes(makeT('th')('labelDubsNote', { n: 2, origin: STRINGS.th.o_assist })));
  assert.equal(STRINGS.en.labelDubsBtn, 'Label all unlabelled dubs with the pack default');
  assert.notEqual(STRINGS.th.labelDubsBtn, STRINGS.en.labelDubsBtn);
});

test('one click labels them all with the default, re-runs the checks and export is allowed', async () => {
  const c = await setup();
  await c.actions.savePackVoice('own', true, { silent: true });
  await c.actions.openModal('export'); await drain();
  const res = await c.actions.labelUnlabelledDubs(P); await drain();
  assert.equal(res.labelled, 2);
  const [call] = labelCalls(c.dev);
  assert.equal(call.args[1].origin, 'own');
  assert.deepEqual(call.args[1].consent, { confirmed: true, statementVersion: 'consent-1' });
  assert.equal(call.args[1].expectedRevision, c.store.get().voiceDefault.revision);
  const s = c.store.get();
  assert.equal(s.exportPreview.checks.find((x) => x.id === 'provenance').severity, 'ok', 'checks re-ran');
  assert.equal(s.exportPreview.ok, true);
  assert.ok(!page(s).includes('data-act="label-dubs"'), 'the button goes once nothing is unlabelled');
  assert.ok(c.toasts.some((t) => t.key === 't_labelled' && t.params?.n === 2));
  assert.ok(STRINGS.th.t_labelled && STRINGS.en.t_labelled);
});

test('without a default the button opens Settings and nothing is labelled', async () => {
  const c = await setup();
  await c.actions.openModal('export'); await drain();
  assert.equal(c.store.get().voiceDefault?.origin ?? null, null);
  const html = page(c.store.get());
  assert.ok(html.includes('data-act="label-dubs"'));
  assert.ok(html.includes(STRINGS.en.labelDubsNoDefault));
  assert.ok(page(c.store.get(), 'th').includes(STRINGS.th.labelDubsNoDefault));

  const res = await c.actions.labelUnlabelledDubs(P); await drain();

  assert.equal(res, null);
  assert.equal(labelCalls(c.dev).length, 0, 'no label request without a default');
  assert.equal(c.store.get().page, 'settings', 'the place to set the default');
  assert.ok(c.toasts.some((t) => t.key === 't_label_need_default'));
  await c.actions.openModal('export'); await drain();
  assert.equal(c.store.get().exportPreview.checks.find((x) => x.id === 'provenance').code, 'provUnlabelled', 'still blocked');
});

test('the backend refusing without a default (cleared elsewhere) sends the creator to Settings; a changed default asks to look again', async () => {
  const c = await setup();
  await c.actions.savePackVoice('own', true, { silent: true });
  await c.actions.openModal('export'); await drain();
  const stale = c.store.get().voiceDefault;
  await c.dev.studioSaveVoiceDefault(P, { expectedRevision: stale.revision, operationId: 'f'.repeat(32), origin: 'ai-generated', consent: { confirmed: true, statementVersion: 'consent-1' } });
  c.store.set({ voiceDefault: stale, packVoices: { [P]: stale } });
  assert.equal(await c.actions.labelUnlabelledDubs(P), null);
  assert.ok(c.toasts.some((t) => t.key === 't_label_default_changed'));
  assert.equal(c.store.get().voiceDefault.origin, 'ai-generated', 'reloaded so the next click names the new default');
  assert.equal(c.store.get().exportPreview.checks.find((x) => x.id === 'provenance').code, 'provUnlabelled');
  assert.ok(STRINGS.th.t_label_default_changed && STRINGS.th.t_label_need_default && STRINGS.th.t_label_none);
});

test('no button when the loader has no voiceLabel feature or the dubs are labelled', async () => {
  const c = await setup();
  await c.actions.savePackVoice('own', true, { silent: true });
  await c.actions.openModal('export'); await drain();
  const s = c.store.get();
  assert.ok(!page({ ...s, features: s.features.filter((f) => f !== 'voiceLabel') }).includes('data-act="label-dubs"'));
  c.dev.devUnlabelDubs(P, []);
  await c.actions.runExportCheck(P);
  assert.ok(!page(c.store.get()).includes('data-act="label-dubs"'));
});
