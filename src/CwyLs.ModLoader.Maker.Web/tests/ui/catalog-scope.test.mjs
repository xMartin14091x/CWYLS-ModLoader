import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createDevBackend } from '../dev/dev-backend.js';
import { createApi, loadCatalog, newOperationId } from '../../ui/api.js';
import { filterLines } from '../../ui/logic/progress.js';
import { startFakeLoader } from '../e2e/fake-loader.mjs';

const P = 'satone-th-dub';
const UI = ['ui_settings', 'ui_continue', 'ui_scene_title'];

test('dev catalog, local search and overall/group progress exclude mixed-in UI localization', async () => {
  const backend = createDevBackend(), api = createApi(backend);
  assert.equal(backend.dev.localization.length, 2172);
  assert.ok(UI.every((id) => backend.dev.localization.some((l) => l.id === id)));
  const { lines } = await loadCatalog(api, P, 'th');
  assert.equal(lines.length, 2169);
  assert.ok(lines.some((l) => !l.voice), 'unvoiced scenario lines stay editable');
  assert.deepEqual(lines.filter((l) => UI.includes(l.id)), []);
  const groups = await api.groups(P, 'th');
  assert.equal(groups.overall.totalLines, 2169);
  assert.equal(groups.groups.reduce((n, g) => n + g.progress.totalLines, 0), 2169);
  assert.equal(groups.groups.some((g) => g.id === 'ui'), false);
  for (const q of ['ui_', 'UI Settings sentinel', 'UI Continue sentinel']) {
    assert.equal((await backend.studioLines({ packId: P, lang: 'th', q, limit: 500 })).total, 0);
    assert.deepEqual(filterLines(lines, { q }), []);
  }
  assert.equal((await api.capabilities()).uiText, false);
  const progress = (await backend.studioExportCheck(P)).checks.find((c) => c.id === 'progress');
  assert.doesNotMatch(progress.message, /ui_/);
  assert.ok(progress.paths.every((path) => !UI.some((id) => path.includes(id))));
});

test('dev direct line reads and subtitle/workflow/take routes reject UI ids', async () => {
  const backend = createDevBackend();
  for (const id of UI) {
    for (const request of [
      () => backend.studioLine(P, 'th', id),
      () => backend.studioHistory(P, 'th', id),
      () => backend.studioTakes(P, 'th', id),
      () => backend.studioSaveSubtitle(P, 'th', id, { expectedRevision: 'r1', operationId: newOperationId(), text: 'Do not edit UI' }),
      () => backend.studioUpdateWorkflow(P, 'th', id, { expectedRevision: 'r1', operationId: newOperationId(), status: 'done' })
    ]) await assert.rejects(request, (e) => e.code === 'not_found');
  }
});

test('fake loader scopes reads, progress and mutations to scenario ids and preserves packed UI subtitles', { timeout: 20000 }, async () => {
  const fake = await startFakeLoader();
  try {
    const headers = { Authorization: `Bearer ${fake.token}` };
    const root = `${fake.base}/v1/maker/studio`;
    const scope = `${root}/packs/sample-thai-dub/languages/th`;
    const get = async (url) => {
      const r = await fetch(url, { headers, signal: AbortSignal.timeout(5000) });
      assert.equal(r.status, 200, await r.clone().text());
      return r.json();
    };
    const page = await get(`${scope}/lines?limit=500`);
    assert.equal(page.total, 40);
    assert.equal(page.items.filter((l) => UI.includes(l.id)).length, 0);
    assert.equal((await get(`${scope}/lines?q=sentinel`)).total, 0);
    const groups = await get(`${scope}/groups`);
    assert.equal(groups.overall.totalLines, 40);
    assert.equal(groups.overall.dubbableLines, 33);
    assert.equal((await get(`${root}/capabilities`)).uiText, false);
    for (const id of UI) {
      for (const suffix of ['', '/history', '/takes']) {
        assert.equal((await fetch(`${scope}/lines/${id}${suffix}`, { headers })).status, 404);
      }
      const r = await fetch(`${scope}/lines/${id}/subtitle`, {
        method: 'PUT', headers: { ...headers, 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: 'Do not edit UI', expectedRevision: 'initial', operationId: newOperationId() })
      });
      assert.equal(r.status, 404);
      assert.equal((await fetch(`${fake.base}/v1/maker/lines/${id}?lang=th`, { headers })).status, 404);
      const v1 = `${fake.base}/v1/maker/packs/sample-thai-dub/subtitles/th/${id}`;
      for (const method of ['PUT', 'DELETE']) {
        assert.equal((await fetch(v1, { method, headers: { ...headers, 'Content-Type': 'application/json' },
          ...(method === 'PUT' ? { body: JSON.stringify({ text: 'Do not edit UI' }) } : {}) })).status, 404);
      }
    }
    const table = JSON.parse(await readFile(join(fake.data, 'sample-thai-dub/data/subtitles/th.json'), 'utf8'));
    assert.equal(table.ui_settings, 'UI translation preserved sentinel');
    const progress = (await get(`${root}/packs/sample-thai-dub/export`)).checks.find((c) => c.id === 'progress');
    assert.ok(progress.paths.every((path) => !UI.some((id) => path.includes(id))));
    assert.doesNotMatch(progress.message, /ui_/);
  } finally { await fake.close(); }
});
