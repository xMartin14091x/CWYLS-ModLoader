import test from 'node:test';
import assert from 'node:assert/strict';
import { qaList, applyFix } from '../../ui/logic/qa.js';
import { readingBasis, readingSpeed, readingClass } from '../../ui/logic/reading.js';
import { glossaryFor, insertTerm, BUILTIN_GLOSSARY } from '../../ui/logic/glossary.js';

const ref = { en: 'Let’s give it {0} minutes together.', ja: '「{0}分、一緒にがんばろう。」' };
const ctx = { lang: 'th', seconds: 3 };

test('QA: a missing placeholder is flagged once even if it appears in both references', () => {
  const out = qaList(ref, 'ลุยด้วยกันนะ', ctx);
  assert.deepEqual(out.filter((x) => x.k === 'ph'), [{ k: 'ph', x: '{0}' }]);
});

test('QA: the placeholder present means no pill', () => {
  assert.deepEqual(qaList(ref, 'ลุยด้วยกัน {0} นาทีนะ', ctx), []);
});

test('QA: a missing tag from the English reference is flagged', () => {
  const out = qaList({ en: 'It is <b>fine</b>.', ja: '' }, 'ไม่เป็นไร', { lang: 'th', seconds: 5 });
  assert.deepEqual(out.map((x) => x.x), ['<b>', '</b>']);
});

test('QA: ๆ at the start of a line, double space and three lines', () => {
  const out = qaList({ en: '', ja: '' }, 'อีกไม่กี่นาทีแล้วนะ  มายืดเส้นยืดสายกันช้า\nๆ ดีกว่า', { lang: 'th', seconds: 20 });
  const kinds = out.map((x) => x.k);
  assert.ok(kinds.includes('mai'));
  assert.ok(kinds.includes('double'));
  const long = qaList({ en: '', ja: '' }, 'ก'.repeat(80), { lang: 'th', seconds: 60 });
  assert.deepEqual(long.filter((x) => x.k === 'lines'), [{ k: 'lines', n: 3 }]);
});

test('QA: reading too fast is flagged above 17 chars/s only', () => {
  const text = 'ก'.repeat(52); // 52 / 3 = 17.3
  assert.ok(qaList({}, text, ctx).some((x) => x.k === 'fast'));
  assert.ok(!qaList({}, 'ก'.repeat(51), ctx).some((x) => x.k === 'fast'), '51 / 3 = 17.0 is not above 17');
});

test('QA: an empty text raises nothing', () => {
  assert.deepEqual(qaList(ref, '   ', ctx), []);
});

test('QA fixes: insert the placeholder, collapse double spaces, join a line that starts with ๆ', () => {
  assert.equal(applyFix('ลุยด้วยกัน', { k: 'ph', x: '{0}' }), 'ลุยด้วยกัน {0}');
  assert.equal(applyFix('a  b   c', { k: 'double' }), 'a b c');
  assert.equal(applyFix('ช้า\nๆ ดีกว่า', { k: 'mai' }), 'ช้าๆ ดีกว่า');
  assert.equal(applyFix('x', { k: 'lines' }), 'x');
});

test('reading basis: the dub when there is one, the original otherwise or on request', () => {
  assert.deepEqual(readingBasis({ origSec: 1.29, dubSec: null }), { seconds: 1.29, source: 'orig', canSwitch: false });
  assert.deepEqual(readingBasis({ origSec: 1.29, dubSec: 1.62 }), { seconds: 1.62, source: 'dub', canSwitch: true });
  assert.deepEqual(readingBasis({ origSec: 1.29, dubSec: 1.62, against: 'orig' }), { seconds: 1.29, source: 'orig', canSwitch: true });
});

test('reading speed and its class, with the mockup line (22 characters over 1.75 s)', () => {
  const text = 'ว่าไง? เมื่อกี้เรียกเรารึเปล่า';
  const cps = readingSpeed(text, 'th', 1.75);
  assert.ok(cps > 11.9 && cps < 12.1, String(cps));
  assert.equal(readingClass(cps, text), 'good');
  assert.equal(readingClass(16, 'x'), 'mid');
  assert.equal(readingClass(17.1, 'x'), 'bad');
  assert.equal(readingClass(0, ''), 'none');
  assert.equal(readingSpeed('x', 'en', 0), 0);
});

test('glossary: Latin terms match whole words, Japanese as substring, target follows the pack language', () => {
  const line = { en: 'Kou-chan is making a nice face. Satone smiles.', ja: 'コウちゃん、サトネ' };
  const out = glossaryFor(line, BUILTIN_GLOSSARY, 'th', 'โคจังหน้าตาดี');
  const by = Object.fromEntries(out.map((x) => [x.id, x]));
  assert.equal(by['kou-chan'].target, 'โคจัง');
  assert.equal(by['kou-chan'].used, true);
  assert.equal(by.satone.target, 'ซาโตเนะ');
  assert.equal(by.satone.used, false);
  assert.equal(by.satoru, undefined);
  assert.equal(glossaryFor({ en: 'a callous remark', ja: '' }, BUILTIN_GLOSSARY, 'th', '').length, 0, '"call" must not match inside "callous"');
});

test('glossary: regex characters in a term cannot break matching', () => {
  const entries = [{ id: 'x', sourceEn: 'C++ (beta)', target: 'ซีพลัสพลัส' }];
  assert.equal(glossaryFor({ en: 'I like C++ (beta) a lot' }, entries, 'th', '').length, 0, 'word-boundary around symbols does not match, but must not throw');
  assert.doesNotThrow(() => glossaryFor({ en: 'x' }, [{ id: 'y', sourceEn: '[(', target: 't' }], 'th', ''));
});

test('insertTerm puts the term at the caret and moves the caret after it', () => {
  assert.deepEqual(insertTerm('ab', 'XY', 1), { text: 'aXYb', caret: 3 });
  assert.deepEqual(insertTerm('ab', 'XY'), { text: 'abXY', caret: 4 });
});
