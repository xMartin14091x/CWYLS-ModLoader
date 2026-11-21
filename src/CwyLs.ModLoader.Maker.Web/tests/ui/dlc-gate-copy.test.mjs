// DLC gate (HMCM, 2026-11-14): the export check `paid` has a label and Thai copy for both backend messages
// (MakerPackChecks.Paid in the Core project). English shows the backend message as it is.
import test from 'node:test';
import assert from 'node:assert/strict';
import { exportCheckDetail } from '../../ui/logic/export-copy.js';
import { STRINGS, makeT } from '../../ui/i18n/i18n.js';

test('the paid DLC export check is labelled and its messages are translated, never the raw key', () => {
  for (const lang of ['en', 'th']) assert.ok(STRINGS[lang].chk_paid, lang);
  const T = makeT('th');
  const found = { id: 'paid', severity: 'error', count: 2,
    message: '2 file(s) have content for paid DLC lines that Pack Maker cannot confirm you own, so the pack is not exported.' };
  assert.equal(exportCheckDetail(found, T, 'th'), T('xc_paidFound', { n: '2' }));
  assert.match(exportCheckDetail(found, T, 'th'), /2/);
  const unknown = { id: 'paid', severity: 'error', count: 0,
    message: 'Pack Maker could not check the pack for paid DLC lines (the game\'s lines did not answer within 3 s); try again in a moment.' };
  assert.equal(exportCheckDetail(unknown, T, 'th'), STRINGS.th.xc_paidUnknown);
  assert.equal(exportCheckDetail(found, makeT('en'), 'en'), found.message);
  assert.notEqual(T('chk_paid'), 'chk_paid');
});
