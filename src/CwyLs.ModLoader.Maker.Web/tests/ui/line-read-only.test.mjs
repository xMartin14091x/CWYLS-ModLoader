// TD-3 (T-059): a line whose id cannot name Studio's file for it (a Windows device name such as "nul", or a trailing dot) is
// marked read-only by the loader (readOnly: "id_not_a_file_name"). The page explains it, does not offer editing or recording,
// and does not ask for its takes (the loader answers 400 for them).
import test from 'node:test';
import assert from 'node:assert/strict';
import { lineVM } from '../../ui/api/model.js';
import { canEditText, canDub } from '../../ui/selectors.js';
import { renderCard } from '../../ui/zones/line-card.js';
import { makeT } from '../../ui/i18n/i18n.js';

const base = { id: 'nul', voice: 'Voice_N', group: 'g', ja: 'ja', en: 'en', workflow: { status: 'todo', starred: false }, revision: 'r1', clipRevision: 'c1' };
const state = (line) => ({
  line, lines: [line], byId: new Map([[line.id, line]]), groups: [{ id: 'g', title: 'G' }], packId: 'p', packLang: 'th', loadingLines: false,
  packs: [{ id: 'p', name: 'P', editable: true, languages: ['th'] }], features: ['subtitles', 'dubs', 'workflow'], capabilities: { features: ['subtitles', 'dubs', 'workflow'] }
});
const strings = { moods: {}, poses: {} };

test('lineVM keeps the read-only code; such a line cannot be edited or dubbed, others still can', () => {
  const ro = lineVM({ ...base, readOnly: 'id_not_a_file_name' }), ok = lineVM({ ...base, id: 'line_a' });
  assert.equal(ro.readOnly, 'id_not_a_file_name');
  assert.equal(ok.readOnly, null);
  assert.equal(canEditText(state(ok)), true);
  assert.equal(canDub(state(ok)), true);
  assert.equal(canEditText(state(ro)), false);
  assert.equal(canDub(state(ro)), false);
});

for (const lang of ['en', 'th']) {
  test(`the card says why the line is read-only (${lang})`, () => {
    const html = renderCard(state(lineVM({ ...base, readOnly: 'id_not_a_file_name' })), { T: makeT(lang), strings, lang });
    assert.match(html, /class="ro-line[ "]/);
    assert.ok(html.includes('“nul”'), 'the id is named');
    assert.match(html, / readonly/);
    assert.match(html, /data-act="star-line" disabled/, 'workflow lives in the same file, so the star is off too');
  });
}
