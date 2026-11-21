import test from 'node:test';
import assert from 'node:assert/strict';
import { sourceNotes, packName, shortText } from '../../ui/logic/source.js';
import { renderSourceNote } from '../../ui/zones/line-card.js';
import { makeT } from '../../ui/i18n/i18n.js';
import { createApi } from '../../ui/api.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { missingSubtitle, missingDub, filterLines, dubDot, FILTERS } from '../../ui/logic/progress.js';

const P = 'satone-th-dub';
const L = (o = {}) => ({ id: 'x', text: '', effectiveText: '', effectivePack: null, ownedDub: false, dubPack: null, voice: 'v', ...o });

test('no note when this pack is what the game uses, or when nothing is used', () => {
  assert.deepEqual(sourceNotes(L({ text: 'a', effectiveText: 'a', effectivePack: P, ownedDub: true, dubPack: P }), P), { sub: null, dub: null });
  assert.deepEqual(sourceNotes(L(), P), { sub: null, dub: null });
  assert.deepEqual(sourceNotes(null, P), { sub: null, dub: null });
});

test('another pack provides the subtitle: "only" when this pack has none, "wins" when it has one and loses', () => {
  const only = sourceNotes(L({ effectiveText: 'theirs', effectivePack: 'other' }), P);
  assert.deepEqual(only.sub, { kind: 'only', packId: 'other', text: 'theirs' });
  const wins = sourceNotes(L({ text: 'mine', effectiveText: 'theirs', effectivePack: 'other' }), P);
  assert.deepEqual(wins.sub, { kind: 'wins', packId: 'other', text: 'theirs' });
  assert.equal(sourceNotes(L({ text: '   ', effectivePack: 'other' }), P).sub.kind, 'only', 'blank text is no text');
});

test('another pack provides the dub: "only" without our dub, "wins" with it', () => {
  assert.deepEqual(sourceNotes(L({ dubPack: 'other' }), P).dub, { kind: 'only', packId: 'other' });
  assert.deepEqual(sourceNotes(L({ dubPack: 'other', ownedDub: true }), P).dub, { kind: 'wins', packId: 'other' });
});

test('pack names come from the pack list, else the id; long subtitles are cut', () => {
  assert.equal(packName([{ id: 'a', name: 'Alpha' }], 'a'), 'Alpha');
  assert.equal(packName([{ id: 'a', name: 'Alpha' }], 'zz'), 'zz');
  assert.equal(packName(undefined, 'zz'), 'zz');
  assert.equal(shortText('a\n  b'), 'a b');
  assert.equal(shortText('x'.repeat(100)).length, 60);
  assert.ok(shortText('x'.repeat(100)).endsWith('…'));
});

test('the note is rendered in both languages with the pack name and is escaped', () => {
  const s = { packId: P, packs: [{ id: 'evil', name: '<b>Evil & Co</b>' }], line: L({ text: 'mine', effectiveText: 'ของเขา <i>', effectivePack: 'evil', dubPack: 'evil' }) };
  for (const lang of ['en', 'th']) {
    const html = renderSourceNote(s, makeT(lang));
    assert.ok(html.includes('&lt;b&gt;Evil &amp; Co&lt;/b&gt;'), `${lang}: pack name escaped`);
    assert.ok(!html.includes('<b>Evil'), `${lang}: no raw markup`);
    assert.equal((html.match(/class="srcrow"/g) || []).length, 2, `${lang}: a subtitle row and a dub row`);
  }
  const only = renderSourceNote({ ...s, line: L({ effectiveText: 'ของเขา <i>', effectivePack: 'evil' }) }, makeT('th'));
  assert.ok(only.includes('ของเขา &lt;i&gt;'), 'the other pack\'s text is shown escaped');
  assert.equal(renderSourceNote({ ...s, line: L({ text: 'a', effectivePack: P, effectiveText: 'a' }) }, makeT('en')), '');
});

test('the dev data has the three cases, mapped through the real model', async () => {
  const api = createApi(createDevBackend({ lines: 60 }));
  const note = async (id) => sourceNotes(await api.line(P, 'th', id), P);
  const a = await note('night_goodnight_002');
  assert.equal(a.sub.kind, 'only'); assert.equal(a.sub.packId, 'thai-subs-community'); assert.equal(a.dub.kind, 'only');
  assert.equal((await note('story01_013')).sub.kind, 'wins');
  assert.equal((await note('story01_011')).dub.kind, 'wins');
  assert.deepEqual(await note('clickheroine_rest_010'), { sub: null, dub: null });
  const list = await api.linesPage(P, 'th', { limit: 60 });
  assert.equal(sourceNotes(list.items.find((l) => l.id === 'night_goodnight_002'), P).sub.kind, 'only', 'the list entries carry the same fields');
});

// ---- the two filters ----
test('missing-subtitle and missing-dub follow CONTRACT-v2: pack-scoped, and text-only lines are never "missing a dub"', () => {
  const lines = [
    L({ id: 'a', text: 'x', ownedDub: true }),
    L({ id: 'b', text: '', ownedDub: false, effectivePack: 'other', effectiveText: 'theirs' }), // another pack provides a subtitle: still missing HERE
    L({ id: 'c', text: 'y', ownedDub: false, dubPack: 'other' }),                                 // another pack provides a dub: still missing HERE
    L({ id: 'd', text: '', voice: null })                                                         // text-only line
  ];
  const ids = (filter) => filterLines(lines, { filter }).map((l) => l.id);
  assert.deepEqual(ids('nosub'), ['b', 'd']);
  assert.deepEqual(ids('nodub'), ['b', 'c']);
  assert.equal(missingSubtitle(L({ text: ' ' })), true);
  assert.equal(missingDub(L({ voice: null })), false);
  assert.ok(FILTERS.includes('nosub') && FILTERS.includes('nodub'));
});

test('a clip made outside Studio shows a filled dub dot and is not "missing a dub"', () => {
  assert.equal(dubDot(L({ ownedDub: true })), 'done');
  assert.equal(dubDot(L({ ownedDub: true, status: 'needs-retake' })), 'retake');
  assert.equal(dubDot(L()), 'none');
  assert.equal(missingDub(L({ ownedDub: true })), false);
});

test('the page\'s filters pick exactly the lines the loader\'s own missing-subtitle / missing-dub filters return (dev oracle, pack-scoped)', async () => {
  const dev = createDevBackend({ lines: 300 });
  const api = createApi(dev);
  const all = [];
  for (let offset = 0; offset < 300; offset += 100) all.push(...(await api.linesPage(P, 'th', { offset, limit: 100 })).items);
  for (const [chip, wire] of [['nosub', 'missing-subtitle'], ['nodub', 'missing-dub']]) {
    const oracle = (await dev.studioLines({ packId: P, lang: 'th', filter: wire, limit: 500 })).items.map((l) => l.id).sort();
    const page = filterLines(all, { filter: chip }).map((l) => l.id).sort();
    assert.deepEqual(page, oracle, chip);
    assert.ok(oracle.length > 0 && oracle.length < all.length, 'a non-trivial filter');
  }
  await assert.rejects(dev.studioLines({ packId: P, lang: 'th', filter: 'retake' }), /Invalid workflow filter/);
});
