import test from 'node:test';
import assert from 'node:assert/strict';
import { authorSlug, suggestPackId, suggestPackName, getLanguageLabel } from '../../ui/logic/pack-naming.js';
import { renderNewPackTab } from '../../ui/zones/packs-modal.js';
import { makeT } from '../../ui/i18n/i18n.js';
import en from '../../ui/i18n/en.js';
import th from '../../ui/i18n/th.js';

const T_EN = makeT('en');
const T_TH = makeT('th');

test('CO pack naming: authorSlug normalises to lowercase [a-z0-9._-] and strips outer punctuation', () => {
  assert.equal(authorSlug('Martin'), 'martin');
  assert.equal(authorSlug('martin'), 'martin');
  assert.equal(authorSlug('Martin Nakorn'), 'martin-nakorn');
  assert.equal(authorSlug('Lan Nguyen'), 'lan-nguyen');
  assert.equal(authorSlug('Martin.dev'), 'martin.dev');
  assert.equal(authorSlug('Martin_1'), 'martin_1');
  assert.equal(authorSlug('  Martin  '), 'martin');
  assert.equal(authorSlug('...martin---'), 'martin');
  assert.equal(authorSlug('Martin & Friends'), 'martin-friends');
  assert.equal(authorSlug(''), 'author', 'empty author falls back to author');
  assert.equal(authorSlug('   '), 'author');
  assert.equal(authorSlug(null), 'author');
  assert.equal(authorSlug('มาร์ติน'), 'author', 'non-ASCII only falls back to author');
});

test('CO pack naming: suggestPackId creates <authorSlug>.<langCode> and complies with ID rules', () => {
  const idTh = suggestPackId('Martin', 'th');
  assert.equal(idTh, 'martin.th');
  assert.match(idTh, /^[a-z0-9][a-z0-9._-]{2,63}$/);

  const idVi = suggestPackId('Martin', 'vi');
  assert.equal(idVi, 'martin.vi');

  const idLan = suggestPackId('Lan Nguyen', 'fr');
  assert.equal(idLan, 'lan-nguyen.fr');
  assert.match(idLan, /^[a-z0-9][a-z0-9._-]{2,63}$/);

  assert.equal(suggestPackId('', 'th'), '');
  assert.equal(suggestPackId('   ', 'th'), '');
});

test('CO pack naming: suggestPackId resolves collisions with <id>.2, <id>.3, etc.', () => {
  // 1. Not taken
  assert.equal(suggestPackId('Martin', 'th', []), 'martin.th');

  // 2. Base ID taken -> suggest <id>.2
  assert.equal(suggestPackId('Martin', 'th', ['martin.th']), 'martin.th.2');
  assert.match(suggestPackId('Martin', 'th', ['martin.th']), /^[a-z0-9][a-z0-9._-]{2,63}$/);

  // 3. Both base ID and .2 taken -> suggest <id>.3
  assert.equal(suggestPackId('Martin', 'th', ['martin.th', 'martin.th.2']), 'martin.th.3');

  // 4. Works with store packs format: array of objects with .id
  const storePacks = [
    { id: 'martin.th' },
    { id: 'martin.th.2' },
    { id: 'other-pack' }
  ];
  assert.equal(suggestPackId('Martin', 'th', storePacks), 'martin.th.3');

  // 5. Works with a Set
  const setPacks = new Set(['martin.th']);
  assert.equal(suggestPackId('Martin', 'th', setPacks), 'martin.th.2');
});

test('CO pack naming: suggestPackName in EN produces "<Author>\'s <Language name>" (e.g. "Martin\'s Thai")', () => {
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'th', T: T_EN }), "Martin's Thai");
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'vi', T: T_EN }), "Martin's Vietnamese");
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'es', T: T_EN }), "Martin's Spanish");
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'fr', T: T_EN }), "Martin's French");
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'de', T: T_EN }), "Martin's German");
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'id', T: T_EN }), "Martin's Indonesian");
  assert.equal(suggestPackName({ author: 'Lan Nguyen', langCode: 'vi', T: T_EN }), "Lan Nguyen's Vietnamese");

  // Custom language display name wins
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'custom', langName: 'Thai Dub', T: T_EN }), "Martin's Thai Dub");

  // Fallback when T is not provided
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'th' }), "Martin's Thai");

  // Empty author returns empty string
  assert.equal(suggestPackName({ author: '', langCode: 'th', T: T_EN }), '');
});

test('CO pack naming: suggestPackName in TH produces "{lang}ของ {author}"', () => {
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'th', T: T_TH }), 'ไทยของ Martin');
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'vi', T: T_TH }), 'เวียดนามของ Martin');
  assert.equal(suggestPackName({ author: 'สมชาย', langCode: 'th', T: T_TH }), 'ไทยของ สมชาย');
  assert.equal(suggestPackName({ author: 'Martin', langCode: 'custom', langName: 'ภาษาเหนือ', T: T_TH }), 'ภาษาเหนือของ Martin');
});

test('CO pack naming: renderNewPackTab prefills suggested name and id when author is provided', () => {
  const state = {
    packs: [],
    packId: null,
    packInfos: {},
    newPackForm: {
      author: 'Martin',
      lang: 'th'
    }
  };

  const html = renderNewPackTab(state, T_EN);

  // Assert suggested name and id are prefilled
  assert.ok(html.includes('id="np-name" value="Martin&#39;s Thai"'), 'prefilled pack name: Martin\'s Thai');
  assert.ok(html.includes('id="np-id" value="martin.th"'), 'prefilled pack id: martin.th');
  assert.ok(html.includes('id="np-author" value="Martin"'), 'author field has Martin');
  assert.ok(html.includes('placeholder="Martin&#39;s Thai"'));
  assert.ok(html.includes('placeholder="martin.th"'));
});

test('CO pack naming: renderNewPackTab suggests <id>.2 when id is taken in existing packs', () => {
  const state = {
    packs: [{ id: 'martin.th' }],
    packId: null,
    packInfos: {},
    newPackForm: {
      author: 'Martin',
      lang: 'th'
    }
  };

  const html = renderNewPackTab(state, T_EN);
  assert.ok(html.includes('id="np-id" value="martin.th.2"'), 'suggests martin.th.2 when martin.th is taken');
});

test('CO pack naming: renderNewPackTab respects user customization flags', () => {
  const state = {
    packs: [],
    packId: null,
    packInfos: {},
    newPackForm: {
      author: 'Martin',
      lang: 'th',
      name: 'My Special Pack',
      nameCustom: true,
      id: 'custom.pack.id',
      idCustom: true
    }
  };

  const html = renderNewPackTab(state, T_EN);
  assert.ok(html.includes('id="np-name" value="My Special Pack"'), 'preserves custom name');
  assert.ok(html.includes('id="np-id" value="custom.pack.id"'), 'preserves custom id');
  assert.ok(html.includes('id="np-name" value="My Special Pack" maxlength="80" placeholder="Martin&#39;s Thai" data-custom="1"'));
  assert.ok(html.includes('id="np-id" value="custom.pack.id" maxlength="64" placeholder="martin.th" data-custom="1"'));
});

// T6: the former "simulated update flow" test re-implemented main.js's custom-field rule in the test itself and could not fail
// when main.js changed, so it was removed. The real flow (updatePackSuggestions in ui/main.js) is DOM-bound; the e2e smoke types into the form.

test('CO pack naming: TH and EN strings exist and have matching placeholders', () => {
  assert.ok(en.newPackNameSuggest, 'en has newPackNameSuggest');
  assert.ok(th.newPackNameSuggest, 'th has newPackNameSuggest');
  assert.ok(en.newPackAuthorDefault, 'en has newPackAuthorDefault');
  assert.ok(th.newPackAuthorDefault, 'th has newPackAuthorDefault');

  const enPlaceholders = (en.newPackNameSuggest.match(/\{\w+\}/g) || []).map((x) => x.replace(/[{}]/g, '')).sort().join(',');
  const thPlaceholders = (th.newPackNameSuggest.match(/\{\w+\}/g) || []).map((x) => x.replace(/[{}]/g, '')).sort().join(',');
  assert.equal(enPlaceholders, 'author,lang');
  assert.equal(thPlaceholders, 'author,lang');
});
