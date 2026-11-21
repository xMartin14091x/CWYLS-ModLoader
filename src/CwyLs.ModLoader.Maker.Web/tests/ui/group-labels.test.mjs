// W2's `labelKey` values (STUDIO-JS.md v2.1, `studioGroupLabels`) must all get a name in both languages; an unknown key falls back to the
// loader's title, then the id (never a raw "group.xyz").
import test from 'node:test';
import assert from 'node:assert/strict';
import { STRINGS } from '../../ui/i18n/i18n.js';
import { groupName, groupStem, humaniseGroupPrefix, mergeDisplayGroups } from '../../ui/selectors.js';

const W2_KEYS = ['group.morning', 'group.focus', 'group.break', 'group.rain', 'group.clickReaction', 'group.evening', 'group.ungrouped'];

test('every labelKey W2 can send is named in Thai and English', () => {
  for (const lang of ['th', 'en']) {
    for (const key of W2_KEYS) {
      const name = groupName({}, { id: 'x', labelKey: key, title: 'RAW-TITLE' }, null, STRINGS[lang]);
      assert.notEqual(name, 'RAW-TITLE', `${lang}: ${key} has no name`);
      assert.ok(!name.startsWith('group.'), `${lang}: ${key} shows its key`);
    }
  }
});

test('an unknown or missing labelKey falls back to the title, then the id', () => {
  assert.equal(groupName({}, { id: 'story_3', labelKey: 'group.futureThing', title: 'Chapter 3' }, null, STRINGS.th), 'Chapter 3');
  assert.equal(groupName({}, { id: 'story_3', labelKey: null, title: 'Chapter 3' }, null, STRINGS.th), 'Chapter 3');
  assert.equal(groupName({}, { id: 'story_3', labelKey: null, title: '' }, null, STRINGS.en), 'story_3');
});

test('mergeDisplayGroups merges per-line groups whose ids differ only by a trailing number and gives readable names in EN & TH', () => {
  const raw = [
    { id: 'morning', labelKey: 'morning', title: 'Morning', order: 0, progress: { totalLines: 6, dubbableLines: 6, subtitlePresent: 5, dubPresent: 3, todo: 1, draft: 1, done: 4, needsRetake: 0, starred: 0 } },
    { id: 'clickheroine_rest_008', labelKey: null, title: 'clickheroine_rest_008', order: 1, progress: { totalLines: 1, dubbableLines: 1, subtitlePresent: 1, dubPresent: 1, todo: 0, draft: 0, done: 1, needsRetake: 0, starred: 0 } },
    { id: 'clickheroine_rest_009', labelKey: null, title: 'clickheroine_rest_009', order: 2, progress: { totalLines: 1, dubbableLines: 1, subtitlePresent: 1, dubPresent: 1, todo: 0, draft: 0, done: 1, needsRetake: 0, starred: 0 } },
    { id: 'clickheroine_rest_010', labelKey: null, title: 'clickheroine_rest_010', order: 3, progress: { totalLines: 1, dubbableLines: 1, subtitlePresent: 1, dubPresent: 1, todo: 0, draft: 0, done: 1, needsRetake: 0, starred: 0 } },
    { id: 'custom_weather_rain_01', labelKey: null, title: 'custom_weather_rain_01', order: 4, progress: { totalLines: 2, dubbableLines: 2, subtitlePresent: 0, dubPresent: 0, todo: 2, draft: 0, done: 0, needsRetake: 0, starred: 0 } },
    { id: 'custom_weather_rain_02', labelKey: null, title: 'custom_weather_rain_02', order: 5, progress: { totalLines: 3, dubbableLines: 3, subtitlePresent: 0, dubPresent: 0, todo: 3, draft: 0, done: 0, needsRetake: 0, starred: 0 } }
  ];
  const merged = mergeDisplayGroups(raw);
  assert.equal(merged.length, 3);
  assert.equal(merged[1].id, 'clickheroine_rest');
  assert.equal(merged[1].progress.totalLines, 3);
  assert.deepEqual(merged[1].rawIds, ['clickheroine_rest_008', 'clickheroine_rest_009', 'clickheroine_rest_010']);
  assert.equal(groupName({}, merged[1], null, STRINGS.en), 'Click reactions (rest)');
  assert.equal(groupName({}, merged[1], null, STRINGS.th), 'ตอนโดนคลิก (ช่วงพัก)');
  assert.equal(merged[2].id, 'custom_weather_rain');
  assert.equal(merged[2].progress.totalLines, 5);
  assert.equal(groupName({}, merged[2], null, STRINGS.en), 'Custom weather rain');
  assert.equal(groupStem('clickheroine_rest_010'), 'clickheroine_rest');
  assert.equal(humaniseGroupPrefix('clickheroine_pat', STRINGS.en), 'Click reactions (pat)');
});
