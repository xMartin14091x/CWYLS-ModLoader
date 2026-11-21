import test from 'node:test';
import assert from 'node:assert/strict';
import { cplCount, cpsCount, maxCpl, tokens, wrapLines, cutAtCount } from '../../ui/logic/thai.js';

const SEG = new Intl.Segmenter('th', { granularity: 'word' });

test('Thai counts base letters only: tone marks and upper/lower vowels are excluded', () => {
  assert.equal(cplCount('ก', 'th'), 1);
  assert.equal(cplCount('ก่', 'th'), 1, 'tone mark');
  assert.equal(cplCount('กิ', 'th'), 1, 'upper vowel');
  assert.equal(cplCount('กุ', 'th'), 1, 'lower vowel');
  assert.equal(cplCount('ก้ำ', 'th'), 2, 'sara am (U+0E33) is a base letter, tone mark is not');
  assert.equal(cplCount('ว่าไง? เมื่อกี้เรียกเรารึเปล่า', 'th'), 22, 'the brief line: 22 base letters (the mockup shows 22/35)');
});

test('stacked marks (vowel + tone) are not counted, mai taikhu and thanthakhat too', () => {
  assert.equal(cplCount('เก็บ', 'th'), 3, 'เ ก บ, mai taikhu excluded');
  assert.equal(cplCount('ลิ้น', 'th'), 2, 'ล and น: upper vowel and tone mark excluded');
  assert.equal(cplCount('การ์ตูน', 'th'), 5, 'thanthakhat and lower vowel excluded');
});

test('other languages count every code point, and surrogate pairs count once', () => {
  assert.equal(cplCount('abc', 'en'), 3);
  assert.equal(cplCount('a😀b', 'en'), 3);
  assert.equal(cplCount('ก่', 'en'), 2, 'only Thai excludes marks');
});

test('reading-speed count ignores whitespace in every language', () => {
  assert.equal(cpsCount('a b\n c', 'en'), 3);
  assert.equal(cpsCount('ก ่ ก', 'th'), 2);
});

test('max characters per line: 35 Thai, 42 otherwise', () => {
  assert.equal(maxCpl('th'), 35);
  assert.equal(maxCpl('en'), 42);
});

test('empty and whitespace text wraps to no lines', () => {
  assert.deepEqual(wrapLines('', 'th'), []);
  assert.deepEqual(wrapLines('  \n ', 'th'), []);
});

test('a short line stays on one line and is returned exactly as typed (no zero-width spaces)', () => {
  const text = 'ว่าไง? เมื่อกี้เรียกเรารึเปล่า';
  const lines = wrapLines(text, 'th', { segmenter: SEG });
  assert.deepEqual(lines, [text]);
  assert.ok(!lines.join('').includes('\u200b'));
});

test('wrap never adds characters: the lines re-join to the text modulo the break whitespace', () => {
  const text = 'อีกไม่กี่นาทีแล้วนะ มายืดเส้นยืดสายกันช้า ๆ ดีกว่าเนอะ แล้วค่อยกลับมาเขียนต่อกันอีกรอบ';
  const lines = wrapLines(text, 'th', { segmenter: SEG });
  assert.ok(lines.length >= 2);
  assert.equal(lines.join('').replace(/\s+/g, ''), text.replace(/\s+/g, ''));
  for (const l of lines) assert.ok(cplCount(l, 'th') <= 35, `${l} is ${cplCount(l, 'th')}`);
});

test('ๆ and closing punctuation stay with the word before and never start a line', () => {
  const words = 'กินข้าวกัน '.repeat(3) + 'เร็ว ๆ';
  for (const max of [14, 15, 16, 17, 18]) {
    const lines = wrapLines(words, 'th', { segmenter: SEG, max });
    for (const l of lines) assert.ok(!/^[ๆฯ]/.test(l.trimStart()), `line starts with ๆ at max ${max}: ${JSON.stringify(lines)}`);
  }
  const t = tokens('เร็ว ๆ นะ', 'th', SEG);
  assert.ok(t.some((x) => x.includes('เร็ว') && x.includes('ๆ')), JSON.stringify(t));
  const p = tokens('ใช่ไหม ?', 'th', SEG);
  assert.ok(p.some((x) => x.includes('?') && x.includes('ไหม')), JSON.stringify(p));
});

test('explicit newlines are kept as line breaks', () => {
  assert.deepEqual(wrapLines('หนึ่ง\nสอง', 'th', { segmenter: SEG }), ['หนึ่ง', 'สอง']);
});

test('a forced cut of an over-long word keeps every mark with its base letter', () => {
  const word = 'กิ่ง'.repeat(20); // 20 base letters ก+ิ+่+ง -> 2 base letters per unit => 40 base letters
  const lines = wrapLines(word, 'th', { segmenter: null, max: 7 });
  for (const l of lines) {
    assert.ok(cplCount(l, 'th') <= 7);
    assert.ok(!/^[\u0E31\u0E34-\u0E3A\u0E47-\u0E4E]/.test(l), `line starts with a combining mark: ${l}`);
  }
  assert.equal(lines.join(''), word);
});

test('cutAtCount returns head and rest that re-join, with trailing marks in the head', () => {
  const [head, rest] = cutAtCount('กิ่งก้าน', 2, 'th');
  assert.equal(head + rest, 'กิ่งก้าน');
  assert.equal(cplCount(head, 'th'), 2);
  assert.ok(head.endsWith('ง'));
});

test('English wraps at 42 on word boundaries', () => {
  const text = 'Somehow the words come easier when someone is with me and the night is quiet.';
  const lines = wrapLines(text, 'en', { segmenter: SEG });
  assert.ok(lines.length >= 2);
  for (const l of lines) assert.ok(cplCount(l, 'en') <= 42);
  assert.equal(lines.join(' '), text);
});

test('without Intl.Segmenter the fallback breaks on spaces only', () => {
  const lines = wrapLines('กินข้าว กินปลา', 'th', { segmenter: null, max: 8 });
  assert.deepEqual(lines, ['กินข้าว', 'กินปลา']);
});
