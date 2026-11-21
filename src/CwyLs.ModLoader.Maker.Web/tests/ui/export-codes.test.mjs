// TD-13 (T-069): the loader sends each export check as code + params; the page words it in the UI language (Thai or English)
// instead of showing the loader's English prose. A check without a code (an older loader) keeps the old message matching.
import test from 'node:test';
import assert from 'node:assert/strict';
import { exportCheckDetail } from '../../ui/logic/export-copy.js';
import { exportPreviewVM } from '../../ui/api/model.js';
import { STRINGS, makeT } from '../../ui/i18n/i18n.js';

// Every code MakerPackChecks sends (keep in step with src/CwyLs.ModLoader.Core/Api/Maker/Studio/MakerPackChecks.cs).
const CODES = ['notChecked', 'linksNever', 'linksOk', 'linksSkipped', 'paidUnknown', 'paidFound', 'manifestMissing', 'manifestInvalid', 'manifestId',
  'manifestOk', 'subBad', 'subEmpty', 'subNone', 'subCount', 'audioBad', 'audioOdd', 'audioCount', 'provNone', 'provMissing', 'provUnlabelled', 'provDuplicate', 'provInvalid', 'provAudio',
  'provConsent', 'provChanged', 'provCount', 'sizeEntries', 'sizeNames', 'sizeMetadata', 'sizeFile', 'sizeLimit', 'sizeBrowser', 'sizeCount', 'progressNone',
  'progressWaiting', 'progressOk', 'progress'];

const check = (o) => ({ id: 'subtitles', severity: 'ok', message: 'English prose from the loader.', count: 0, paths: [], ...o });

test('the preview view model keeps code and params', () => {
  const vm = exportPreviewVM({ packId: 'p', checks: [{ id: 'audio', severity: 'ok', message: 'm', count: 1, paths: [], code: 'audioCount', params: { n: 1 } }] });
  assert.deepEqual([vm.checks[0].code, vm.checks[0].params], ['audioCount', { n: 1 }]);
  assert.deepEqual([exportPreviewVM({ packId: 'p', checks: [{ id: 'audio' }] }).checks[0].code, exportPreviewVM({ packId: 'p', checks: [{ id: 'audio' }] }).checks[0].params], [null, {}]);
});

test('every code has Thai and English words', () => {
  for (const code of CODES) {
    assert.equal(typeof STRINGS.th['xc_' + code], 'string', `th xc_${code}`);
    assert.equal(typeof STRINGS.en['xc_' + code], 'string', `en xc_${code}`);
  }
});

test('a coded check is worded from its code and params in both languages, never the loader prose', () => {
  for (const lang of ['th', 'en']) {
    const T = makeT(lang);
    const text = exportCheckDetail(check({ code: 'subCount', params: { n: 12, m: 2 } }), T, lang);
    assert.equal(text, T('xc_subCount', { n: 12, m: 2 }));
    assert.ok(!text.includes('English prose'));
    assert.equal(exportCheckDetail(check({ id: 'manifest', code: 'manifestOk', params: { name: 'My pack', version: '1.2.0' } }), T, lang),
      lang === 'th' ? 'My pack เวอร์ชัน 1.2.0' : 'My pack 1.2.0');
  }
  const th = makeT('th');
  const progress = check({ id: 'progress', severity: 'warning', code: 'progress',
    params: { langs: [{ lang: 'th', dub: 3, voiced: 8, sub: 1, total: 10 }, { lang: 'en', dub: 0, voiced: 8, sub: 2, total: 10 }] } });
  assert.equal(exportCheckDetail(progress, th, 'th'),
    th('xc_progress', { lang: 'th', dub: 3, voiced: 8, sub: 1, total: 10 }) + '; ' + th('xc_progress', { lang: 'en', dub: 0, voiced: 8, sub: 2, total: 10 }));
});

test('a check without a code (older loader) still uses the old matching; an unknown code falls back the same way', () => {
  const th = makeT('th');
  assert.equal(exportCheckDetail(check({ message: 'No subtitle tables.' }), th, 'th'), STRINGS.th.xc_subNone);
  assert.equal(exportCheckDetail(check({ message: 'No subtitle tables.', code: 'fromTheFuture', params: {} }), th, 'th'), STRINGS.th.xc_subNone);
  assert.equal(exportCheckDetail(check({ message: 'No subtitle tables.' }), makeT('en'), 'en'), 'No subtitle tables.');
});
