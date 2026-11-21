// Content-level click-through of the Studio page on W2's REAL HTTP adapter against W2's loopback fake loader v2 (which advertises
// catalog, workflow, subtitles, dubs, projects, newPacks; playback is absent). The page is served by the fake itself (token injected
// into index.html). Covers the default flip (?ui=1 vs plain URL), text/workflow/history/filters/source-pack/spoken/conflicts, the dub
// deck (mic setup, record, consent, trim, even-out, publish, local playback, import, 3-take limit, unpublish), pack creation, offline
// and 401 on the real HTTP adapter.
// Run bounded:   perl -e 'alarm 240; exec @ARGV' node tests/e2e/smoke-fake.mjs
import assert from 'node:assert/strict';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { launchChrome, openPage, setViewport, loadStudio, screenshot } from './cdp.mjs';
import { encodeWavPcm16Mono } from '../../ui/audio/wav.js';
import { startFakeLoader } from './fake-loader.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
setTimeout(() => { console.error('WATCHDOG: smoke-fake took too long'); process.exit(2); }, 210000).unref();
const shots = process.argv.includes('--shots') ? process.argv[process.argv.indexOf('--shots') + 1] : null;

const fake = await startFakeLoader({ tickSec: 3 }); // the fake says a voiced line aloud every 3 s (line.spoken, like the game)
const chrome = await launchChrome({ extraArgs: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] });
let steps = 0;
try {
  const page = await openPage(chrome.port);
  await page.send('Page.addScriptToEvaluateOnNewDocument', { source: 'window.__gum = 0; window.__gumArgs = []; const o = navigator.mediaDevices && navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices); if (o) navigator.mediaDevices.getUserMedia = (c) => { window.__gum++; window.__gumArgs.push(JSON.stringify(c)); return o(c); };' });
  await setViewport(page, 1440, 900);
  const q = (expr) => page.eval(expr);
  const until = async (expr, what, ms = 6000) => {
    const t0 = Date.now();
    while (Date.now() - t0 < ms) { if (await q(expr)) return; await sleep(50); }
    const where = await q("JSON.stringify({count:(document.querySelector('#l-count')||{}).textContent, banner:(document.querySelector('#banner')||{}).className, lid:(document.querySelector('.lid')||{}).textContent, save:(document.querySelector('.save')||{}).textContent})").catch(() => '?');
    throw new Error(`timed out waiting for: ${what} (page: ${where})`);
  };
  const step = async (name, fn) => { await fn(); steps++; console.log(`ok  ${String(steps).padStart(2)} ${name}`); };
  const url = (lang = 'en', extra = '') => `${fake.url}index.html?backend=http&lang=${lang}${extra}`;
  const click = (sel) => q(`document.querySelector(${JSON.stringify(sel)}).click()`);
  const text = (sel) => q(`(document.querySelector(${JSON.stringify(sel)})||{}).textContent||''`);
  const count = (sel) => q(`document.querySelectorAll(${JSON.stringify(sel)}).length`);
  const key = (k, shift = false) => q(`document.body.dispatchEvent(new KeyboardEvent('keydown',{key:${JSON.stringify(k)},shiftKey:${shift},bubbles:true,cancelable:true}))`);
  const type = (value) => q(`(()=>{const e=document.querySelector('#ed');e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('input',{bubbles:true}));})()`);
  // the page's own adapter instance (a module singleton), used only to LOOK at what the loader holds or to act as "another tab"
  const raw = (expr) => q(`(async()=>{const r=(await import('./modules/loader/index.js')).default;return JSON.stringify(${expr});})()`).then(JSON.parse);
  const PACK = 'sample-thai-dub', LANG = 'th';
  const lineNow = (id) => raw(`await r.studioLine('${PACK}','${LANG}','${id}')`);
  const takesNow = (id) => raw(`await r.studioTakes('${PACK}','${LANG}','${id}')`);
  const lidNow = () => text('.lid');
  const bump = (id, txt, op) => raw(`await (async()=>{const l=await r.studioLine('${PACK}','${LANG}','${id}');return r.studioSaveSubtitle('${PACK}','${LANG}','${id}',{expectedRevision:l.revision,operationId:'${op}',text:${JSON.stringify(txt)}});})()`);
  const selectRow = async (id) => {
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');if(s&&s.value){s.value='';s.dispatchEvent(new Event('change',{bubbles:true}));}const i=document.querySelector('[data-in=q]');if(i&&i.value){i.value='';i.dispatchEvent(new Event('input',{bubbles:true}));}})()");
    // A return to Voice lines can retain its scroll offset; search the full list from the top.
    await q("(() => { const l = document.getElementById('vb-list'); if (l) { l.scrollTop = 0; l.dispatchEvent(new Event('scroll')); } })()");
    await until("document.querySelector('.vb-row[data-vb-index=\"0\"]') && document.getElementById('vb-list').scrollTop === 0", 'Voice lines search starts at the first row');
    // The Voice lines list only draws the rows near the viewport: scroll a page at a time, as a user would.
    await until(`(() => { if (document.querySelector('.vb-row[data-id="${id}"]')) return true; const l = document.getElementById('vb-list'); if (l) { l.scrollTop += l.clientHeight; l.dispatchEvent(new Event('scroll')); } return false; })()`, `vb-row ${id}`, 15000);
    // Let the windowed list finish repainting after the scroll, then click; retry once if a repaint ate the click.
    const selected = `document.querySelector('.lid') && document.querySelector('.lid').textContent === '${id}'`;
    for (let attempt = 0; attempt < 2; attempt++) {
      await q('new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))');
      await click(`.vb-row[data-id="${id}"]`);
      try { await until(selected, `line ${id} selected`, 10000); break; } catch (e) { if (attempt) throw e; }
    }
  };

  await step('the plain URL opens Studio v2 by default on the real adapter (and ?ui=1 still opens v1): Thai UI, 5 pages, six scenes, 40 lines, dubs, playback and newPacks enabled', async () => {
    await page.send('Page.navigate', { url: `${fake.url}index.html?ui=1&backend=http` });
    await until("!!document.querySelector('#app') && document.querySelector('#app').children.length > 0", 'v1 renders into #app when ?ui=1 is requested', 10000);
    assert.equal(await count('#studio'), 0, 'v2 shell must not exist on ?ui=1');
    page.problems.length = 0;

    await loadStudio(page, url('th'));
    assert.equal(await q('document.documentElement.lang'), 'th');
    assert.equal(await count('.top-tab'), 5);
    assert.equal(await count('.scn-nav'), 1);
    assert.equal(await lidNow(), 'sample_01');
    assert.ok((await q("document.querySelector('#ed').value")).length > 0, 'the line card shows the pack\'s text');
    assert.equal(await q("document.querySelector('#banner').hidden"), true, 'all features including playback are advertised by the fake loader');
    assert.equal(await q("document.querySelector('#ed').readOnly"), false, 'the editable pack can be typed in');
    assert.equal(await q("document.querySelector('[data-act=setup-mic]').disabled"), false, 'dubs is advertised so the mic button is enabled');
    assert.equal(await q("document.querySelector('[data-act=play-orig]').disabled"), false, 'playback is advertised so play-orig is enabled');
    assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), false, 'play-mine is enabled');
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    assert.match(await text('#l-count'), /^40\s/);
    assert.match(await text('.prog-sub .v'), /\/40$/, 'top bar counts story subtitles only');
    assert.match(await text('.prog-dub .v'), /\/33$/, 'the voiced UI sentinel adds no dub work');
    assert.equal(await raw("(await r.studioLines({packId:'sample-thai-dub',lang:'th',limit:500})).items.some(l => l.id.startsWith('ui_'))"), false, 'UI strings never reach Voice lines');
    assert.deepEqual(await q("[...document.querySelectorAll('[data-ch=scnfilter] option')].slice(1).map((e) => e.textContent.replace(/\\s*\\(\\d+\\)$/, ''))"), ['ยามเช้า', 'ช่วงโฟกัส', 'ช่วงพัก', 'วันฝนตก', 'ตอนโดนคลิก', 'ยามเย็น'], 'W2\'s labelKeys (group.morning ...) get the UI\'s own Thai names, not the loader\'s raw titles');
    await click('#top [data-act="page-tab"][data-id="studio"]');
    await until("document.querySelector('.lid')", 'back on Studio');
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('.mi-new')", 'the picker');
    assert.equal(await q("document.querySelector('.mi-new').disabled"), false, 'pack creation is enabled with the newPacks capability');
    await click('[data-act="pack-menu"]');
    await until("!document.querySelector('.pack-menu')", 'the picker closed');
    if (shots) await screenshot(page, `${shots}/fake-th-normal.png`);
  });

  await step('no outside requests and no console problems; the loader is never asked to play', async () => {
    assert.deepEqual(page.requests.filter((u) => !u.startsWith(fake.base) && !u.startsWith('data:')), []);
    assert.deepEqual(page.problems, []);
    assert.deepEqual(page.requests.filter((u) => /\/v1\/maker\/.*\/play/.test(u)), [], 'no play routes');
  });

  await step('English: the footer does not claim "live in game" for a line nothing was saved on; the same fixture content in English', async () => {
    await loadStudio(page, url('en'));
    assert.match(await text('.save'), /^Saved to pack$/, `got: ${await text('.save')}`);
    assert.match(await text('.brand-studio'), /Dub & Subtitle Studio/);
  });

  await step('subtitle-only Play my version settles text and sends sub without publishing or uploading audio', async () => {
    await selectRow('sample_04');
    const before = await lineNow('sample_04');
    assert.equal(before.ownedDub, false);
    assert.equal((await takesNow('sample_04')).items.length, 0);
    const plays = [], writes = [];
    page.on('Network.requestWillBeSent', ({request}) => {
      if (request.url.endsWith('/v1/maker/studio/play')) plays.push(JSON.parse(request.postData));
      if (/\/(publish|audio)(?:\?|$)/.test(request.url) && request.method !== 'GET') writes.push(request.url);
    });
    await type('My subtitle-only version');
    await click('[data-act="play-mine"]');
    await until("document.querySelector('.hb-mine.busy') && [...document.querySelectorAll('.toast')].some(t => t.textContent.includes('original voice'))", 'subtitle-only playback acknowledgement');
    assert.equal(plays.length, 1);
    assert.deepEqual([plays[0].id, plays[0].variant, plays[0].lang, plays[0].packId], ['sample_04', 'sub', 'th', PACK]);
    // TD-34: like the game, the fake speaks the Play as a runtime cwy_say_* row; "Satone just said" names sample_04 (the open line).
    await until("document.querySelector('.said-here')", '"Satone just said" names the line played', 4000);
    assert.doesNotMatch(await text('.said'), /cwy_say_|not in this list|ไม่มีในรายการ/);
    assert.ok(plays[0].expectedRevision);
    assert.deepEqual(writes, []);
    assert.equal((await lineNow('sample_04')).ownedSubtitle, 'My subtitle-only version');
    assert.equal((await takesNow('sample_04')).items.length, 0);
  });

  await step('Voice lines page: a scene filters the list and the count equals what the loader says; "All scenes" clears it', async () => {
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    const gid = await q("document.querySelectorAll('[data-ch=scnfilter] option')[2].value");
    await q(`(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value=${JSON.stringify(gid)};s.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    await until(`document.querySelector('.vb-row') && document.querySelector('#l-count').textContent.trim() !== '40 lines'`, 'a filtered list');
    const ui = parseInt(await text('#l-count'), 10);
    const loader = await raw(`(await r.studioLines({packId:'${PACK}',lang:'${LANG}',group:${JSON.stringify(gid)},limit:500})).total`);
    assert.equal(ui, loader, `scene ${gid}: UI ${ui}, loader ${loader}`);
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    await until("/^40\\s/.test(document.querySelector('#l-count').textContent)", 'all lines again');
  });

  await step('search narrows by text; selecting by click opens Studio and ArrowDown moves through the list', async () => {
    const t = (await lineNow('sample_03')).subtitle || (await lineNow('sample_03')).ownedSubtitle;
    assert.ok(t, 'sample_03 has some subtitle in the fake');
    const word = t.split(/\s+/)[0];
    await q(`(()=>{const i=document.querySelector('[data-in=q]');i.value=${JSON.stringify(word)};i.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    await until(`document.querySelector('#l-count').textContent.trim() !== '40 lines' && document.querySelectorAll('.vb-row').length >= 1`, 'search results', 8000);
    assert.ok((await q("[...document.querySelectorAll('.vb-row')].some((r) => r.dataset.id === 'sample_03')")), 'the line with that word is among them');
    await q("(()=>{const i=document.querySelector('[data-in=q]');i.value='';i.dispatchEvent(new Event('input',{bubbles:true}));})()");
    await until("/^40\\s/.test(document.querySelector('#l-count').textContent)", 'cleared');
    await selectRow('sample_01');
    await q("document.body.focus(); document.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}))");
    await until("document.querySelector('.lid').textContent === 'sample_02'", 'ArrowDown moved on');
  });

  await step('typing autosaves after the pause: the loader holds the text; the footer says "Saved to pack" and does NOT claim live (the fake has no game)', async () => {
    await selectRow('sample_02');
    await type('ก'.repeat(30));
    await until("document.querySelector('.save') && /Saving/i.test(document.querySelector('.save').textContent)", 'the saving note');
    await until("document.querySelector('.save') && /^Saved to pack/.test(document.querySelector('.save').textContent)", 'the saved note', 7000);
    const l = await lineNow('sample_02');
    assert.equal(l.ownedSubtitle, 'ก'.repeat(30), 'the loader really holds it');
    assert.doesNotMatch(await text('.save'), /live in game/, `footer: ${await text('.save')}`);
    assert.match(await text('.row.on .row-t'), /^ก{30}$/);
  });

  await step('the history menu lists revisions and reverting puts the older text back into the box and into the loader', async () => {
    await type('ข'.repeat(20));
    await until("document.querySelector('.save') && /^Saved to pack/.test(document.querySelector('.save').textContent) && document.querySelector('#ed').value === 'ข'.repeat(20)", 'second save', 7000);
    await sleep(900);
    await click('[data-act="hist"]');
    await until("document.querySelectorAll('.hm-i').length >= 2", 'history items', 6000);
    const before = await q("[...document.querySelectorAll('.hm-i')].map((i) => i.querySelector('.hm-t').textContent)");
    const older = 'ก'.repeat(30);
    const idx = before.findIndex((t) => t === older);
    assert.ok(idx >= 0, `the older text is in the history: ${JSON.stringify(before)}`);
    await q(`document.querySelectorAll('.hm-i')[${idx}].querySelector('[data-act=revert]').click()`);
    await until(`document.querySelector('#ed').value === ${JSON.stringify(older)}`, 'the older text in the box', 6000);
    await sleep(600);
    assert.equal((await lineNow('sample_02')).ownedSubtitle, older);
  });

  await step('status and star reach the loader and the screen; "needs retake" is not offered on a line with no takes (it means re-record the dub)', async () => {
    await selectRow('sample_04');
    await click('[data-act="status"][data-id="draft"]');
    await until("document.querySelector('.seg-b.on.s-draft')", 'draft chip');
    await click('#star-btn');
    await until("document.querySelector('#star-btn.on')", 'starred');
    await sleep(700);
    const l = await lineNow('sample_04');
    assert.deepEqual([l.workflow.status, l.workflow.starred], ['draft', true], JSON.stringify(l.workflow));
    assert.equal(await q("document.querySelector('[data-act=retake]').disabled"), true);
    await click('[data-act="status"][data-id="done"]');
    await sleep(700);
    assert.equal((await lineNow('sample_04')).workflow.status, 'done');
    await q("document.querySelector('#star-btn').click()");
    await sleep(500);
    assert.equal((await lineNow('sample_04')).workflow.starred, false, 'unstarring reaches the loader too');
  });

  await step('each status chip shows the same number of lines as the loader\'s own filter', async () => {
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    const pairs = [['todo', 'todo'], ['draft', 'draft'], ['done', 'done'], ['retake', 'needs-retake']];
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    for (const [chip, wire] of pairs) {
      const loader = await raw(`(await r.studioLines({packId:'${PACK}',lang:'${LANG}',filter:'${wire}',limit:500})).total`);
      await click(`[data-act="filter"][data-id="${chip}"]`);
      // Wait for the list to apply the filter (a fixed 400 ms sleep read the "All" count on a busy machine), then compare.
      await until(`(parseInt((document.querySelector('#l-count')||{}).textContent,10)||0) === ${loader}`, `chip ${chip} count settles`, 5000).catch(() => {});
      const ui = parseInt(await text('#l-count'), 10) || 0;
      assert.equal(ui, loader, `chip ${chip}: UI ${ui}, loader ${loader}`);
    }
    await click('[data-act="filter"][data-id="all"]');
  });

  await step('the No subtitle and No dub chips equal the REAL loader\'s missing-subtitle and missing-dub totals (pack-scoped)', async () => {
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    for (const [chip, wire] of [['nosub', 'missing-subtitle'], ['nodub', 'missing-dub']]) {
      await click(`[data-act="filter"][data-id="${chip}"]`);
      await until(`document.querySelector('.chip-${chip}.on')`, 'chip on');
      await sleep(300);
      const ui = parseInt(await text('#l-count'), 10) || 0;
      const loader = await raw(`(await r.studioLines({packId:'${PACK}',lang:'${LANG}',filter:'${wire}',limit:500})).total`);
      assert.equal(ui, loader, `chip ${chip}: UI ${ui}, loader ${loader}`);
      assert.ok(loader > 0, 'the fake has such lines');
    }
    await click('[data-act="filter"][data-id="all"]');
    await click('#top [data-act="page-tab"][data-id="studio"]');
    await until("document.querySelector('.lid')", 'back on Studio');
  });

  await step('for every line the page names another pack exactly when the loader says another pack wins (subtitle and dub)', async () => {
    const all = await raw(`(await r.studioLines({packId:'${PACK}',lang:'${LANG}',limit:500})).items.map((l) => ({id:l.id, sub:l.subtitlePack, dub:l.dubPack, own:l.ownedSubtitle}))`);
    const expected = (l) => (l.sub && l.sub !== PACK ? 1 : 0) + (l.dub && l.dub !== PACK ? 1 : 0);
    const withNote = all.filter((l) => expected(l) > 0), without = all.filter((l) => expected(l) === 0).slice(0, 3);
    assert.ok(withNote.length > 0, 'the fake has lines where another pack provides the subtitle');
    const pick = async (id) => {
      await selectRow(id);
    };
    for (const l of [...withNote, ...without]) {
      await pick(l.id);
      await sleep(150);
      assert.equal(await count('.srcrow'), expected(l), `${l.id}: sub ${l.sub}, dub ${l.dub}`);
    }
    const first = withNote[0];
    await pick(first.id);
    assert.match(await text('#srcnote'), /Sample Reference Pack/, 'the other pack is named by its loader name, not its id');
    if (shots) await screenshot(page, `${shots}/fake-en-source-pack.png`);
  });

  await step('Open line on a REAL line.spoken event from the fake: the line it names is brought up; the languages agree (game and pack are Thai) so there is no language notice', async () => {
    await selectRow('sample_30');
    await until("document.querySelector('.said-open')", 'the fake says a line and the button appears', 9000);
    const spoken = await q("document.querySelector('.said-open').dataset.id");
    assert.notEqual(spoken, 'sample_30');
    await click('.said-open');
    await until(`document.querySelector('.lid').textContent === ${JSON.stringify(spoken)}`, `the spoken line ${spoken} is open`, 4000);
    assert.equal(await count('.mir-lang'), 0, 'game and pack are both Thai');
    assert.equal(await count('.langwarn'), 0);
  });

  await step('a change made by "another tab" appears on screen without a reload (events and refetch)', async () => {
    await selectRow('sample_06');
    await bump('sample_05', 'จากอีกแท็บ', 'a'.repeat(32));
    await until("[...document.querySelectorAll('.row')].some((r) => r.dataset.id === 'sample_05' && r.textContent.includes('จากอีกแท็บ'))", 'the row shows the other tab\'s text', 12000);
    await selectRow('sample_05');
    assert.equal(await q("document.querySelector('#ed').value"), 'จากอีกแท็บ');
  });

  await step('a conflict keeps the draft, offers the pack\'s version, and both choices work', async () => {
    await selectRow('sample_07');
    await type('ของฉันหนึ่ง');
    await bump('sample_07', 'จากแท็บอื่นหนึ่ง', 'f'.repeat(32));
    await until("document.querySelector('.cflt')", 'the conflict row', 8000);
    assert.match(await text('.cflt'), /จากแท็บอื่นหนึ่ง/);
    assert.equal(await q("document.querySelector('#ed').value"), 'ของฉันหนึ่ง', 'the draft is untouched');
    await click('[data-act="conflict-theirs"]');
    await until("!document.querySelector('.cflt') && document.querySelector('#ed').value === 'จากแท็บอื่นหนึ่ง'", 'theirs adopted');
    await type('ของฉันสอง');
    await bump('sample_07', 'จากแท็บอื่นสอง', 'e'.repeat(32));
    await until("document.querySelector('.cflt')", 'second conflict', 8000);
    await click('[data-act="conflict-mine"]');
    await until("!document.querySelector('.cflt') && document.querySelector('.save') && /^Saved to pack/.test(document.querySelector('.save').textContent)", 'mine saved', 8000);
    assert.equal((await lineNow('sample_07')).ownedSubtitle, 'ของฉันสอง');
  });

  await step('Enter saves at once and moves to the next line; a reload shows the saved text', async () => {
    await selectRow('sample_08');
    await type('ก่อนกด Enter');
    await q("document.querySelector('#ed').focus(); document.querySelector('#ed').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))");
    await until("document.querySelector('.lid').textContent === 'sample_09'", 'next line');
    await sleep(500);
    assert.equal((await lineNow('sample_08')).ownedSubtitle, 'ก่อนกด Enter');
    await loadStudio(page, url('en'));
    await selectRow('sample_08');
    assert.equal(await q("document.querySelector('#ed').value"), 'ก่อนกด Enter', 'it survived a reload (the loader keeps it)');
  });

  await step('the pack picker lists the packs; the reference pack is read-only with the loader\'s reason as plain text; switching back edits again', async () => {
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('.pack-menu')", 'the picker');
    assert.ok((await count('.pack-menu .mi')) >= 2);
    assert.ok((await count('.pack-menu .mi.ro')) >= 1, 'a read-only pack is listed as such');
    const roId = await q("document.querySelector('.pack-menu .mi.ro').dataset.id");
    await click(`.pack-menu .mi.ro`);
    await until("document.querySelector('#banner').classList.contains('b-ro') && document.querySelector('#ed')?.readOnly === true", 'the read-only destination editor', 8000);
    assert.equal(await q("document.querySelector('#ed').readOnly"), true);
    const reason = (await raw("(await r.studioPacks()).packs.find((p) => !p.studioEditable).studioReadOnlyReason"));
    assert.ok(reason === null || typeof reason === 'string');
    if (shots) await screenshot(page, `${shots}/fake-en-readonly.png`);
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('.pack-menu')", 'the picker again');
    await click('.pack-menu .mi:not(.ro):not(.mi-new)');
    await until("!document.querySelector('#banner').classList.contains('b-ro') && document.querySelector('#ed')?.readOnly === false", 'editable again', 8000);
    assert.ok(roId, 'had an id');
  });

  const wavPath = join(tmpdir(), 'cwyls-smoke-fake-take.wav');
  writeFileSync(wavPath, encodeWavPcm16Mono(Float32Array.from({ length: 44100 }, (_, i) => 0.3 * Math.sin(2 * Math.PI * 220 * i / 44100)), 44100));
  const pickFile = async () => {
    await page.send('DOM.enable');
    const { root } = await page.send('DOM.getDocument');
    const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: '#file' });
    await page.send('DOM.setFileInputFiles', { files: [wavPath], nodeId });
  };

  await step('the microphone is asked for only on the click, with echo cancellation, noise suppression and auto gain OFF; the halo follows the input', async () => {
    await loadStudio(page, url('en'));
    assert.equal(await q('window.__gum'), 0, 'not asked at load');
    await click('[data-act="setup-mic"]');
    await until("document.querySelector('.recwrap:not(.off)')", 'mic ready', 10000);
    assert.equal(await q('window.__gum'), 1);
    const c = JSON.parse(await q('window.__gumArgs[0]'));
    assert.deepEqual([c.audio.echoCancellation, c.audio.noiseSuppression, c.audio.autoGainControl], [false, false, false]);
    await until("parseFloat(document.querySelector('.recwrap') && document.querySelector('.recwrap').style.getPropertyValue('--lvl') || '0') > 0.05", 'a level on the halo (the fake microphone beeps)', 8000);
  });

  await step('R records after a silent 3-2-1 on the real HTTP adapter; the take is saved with its audio and shows a waveform', async () => {
    await selectRow('sample_09');
    await key('r');
    await until("document.querySelector('.count')", 'the count-in', 4000);
    assert.equal((await takesNow('sample_09')).items.length, 0, 'nothing exists during the count-in');
    await until("document.querySelector('.recbadge')", 'recording', 8000);
    await sleep(1800);
    await key('r');
    await until("document.querySelectorAll('.tk').length === 1", 'the take pill', 8000);
    const t = (await takesNow('sample_09')).items[0];
    assert.deepEqual([t.origin, t.consent.confirmed, t.consent.statementVersion, t.sourcePresent], ['own', false, 'consent-1', true]);
    assert.ok(t.sourceDurationSec > 1.2 && t.sourceDurationSec < 3.5, String(t.sourceDurationSec));
    await until("document.querySelectorAll('.wave .wsvg rect').length >= 10", 'a waveform', 5000);
  });

  await step('the consent statement is shown in full; changing origin needs fresh consent and an explicit save', async () => {
    await click('[data-ch="consent"]');
    await until("document.querySelector('.consent-menu')", 'the statement');
    assert.equal(await q("document.querySelector('[data-ch=consent]').checked"), false, 'ticking alone does not confirm');
    assert.equal(await text('.consent-menu .cm-t'), "I confirm this take is my own voice, or I have the speaker's permission to use it in this pack, and the origin I picked is true. I will not present an AI voice as the original voice actor.");
    await click('[data-act="consent-confirm"]');
    await until("document.querySelector('[data-ch=consent]').checked", 'ticked');
    const t = (await takesNow('sample_09')).items[0];
    assert.deepEqual([t.consent.confirmed, t.consent.statementVersion], [true, 'consent-1']);
    for (const origin of ['ai-assisted', 'own']) {
      await click(`[data-act="origin"][data-id="${origin}"]`);
      await until("document.querySelector('[data-act=take-origin-save]')", 'explicit take save');
      assert.equal(await q("document.querySelector('[data-act=take-origin-save]').disabled"), true);
      await click('[data-ch="take-origin-consent"]');
      await until("document.querySelector('.consent-menu')", 'the statement for this origin');
      await click('[data-act="consent-confirm"]');
      await until("document.querySelector('[data-ch=take-origin-consent]').checked", 'draft consent ticked');
      await click('[data-act="take-origin-save"]');
      await until("!document.querySelector('[data-act=take-origin-save]')", 'take origin saved');
    }
  });

  await step('dragging a trim handle only moves the screen; letting go sends the trim; even-out is sent too', async () => {
    const drag = (sel, from, to) => q(`(()=>{const h=document.querySelector('${sel}');const w=document.getElementById('wave').getBoundingClientRect();const mk=(t,f)=>new PointerEvent(t,{bubbles:true,clientX:w.left+w.width*f,clientY:w.top+5,pointerId:7,isPrimary:true});h.dispatchEvent(mk('pointerdown',${from}));h.dispatchEvent(mk('pointermove',${to}));return true;})()`);
    await drag('.handle.l', 0.02, 0.3);
    await until("document.querySelector('.trim-dim.l') && parseFloat(document.querySelector('.trim-dim.l').style.width) > 20", 'the screen follows the pointer');
    assert.equal((await takesNow('sample_09')).items[0].trimStartSec ?? 0, 0, 'nothing sent while dragging');
    await q("document.getElementById('deck').dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerId:7}))");
    await sleep(500);
    assert.ok((await takesNow('sample_09')).items[0].trimStartSec > 0.2, 'sent on release');
    await click('[data-ch="even"]');
    await sleep(400);
    assert.equal((await takesNow('sample_09')).items[0].evenOutVolume, true);
  });

  await step('T prepares and uploads the render, publishes the take into the pack, and plays on the real adapter', async () => {
    const takeId = (await takesNow('sample_09')).items[0].id;
    await key('t');
    await until("document.querySelector('.tk.pub .pubdot')", 'the take is published in the pack', 10000);
    const l = await lineNow('sample_09');
    const t = (await takesNow('sample_09')).items[0];
    assert.deepEqual([l.ownedDub, l.publishedTakeId, t.renderReady], [true, takeId, true]);
    assert.equal(await q("document.querySelector('[data-act=retake]').disabled"), false, 'Needs retake is enabled once the line has takes');
    await until("document.querySelector('.hb-mine.busy')", 'the fake loader answered /v1/maker/studio/play', 6000);
    assert.equal(page.requests.filter((u) => /\/v1\/maker\/studio\/play$/.test(u)).length, 1, 'studio play route called once');
  });

  await step('Space plays the take locally and stops it again', async () => {
    await key(' ');
    await until("document.querySelector('.playtake.on')", 'local playback', 5000);
    await key(' ');
    await until("!document.querySelector('.playtake.on')", 'stopped');
  });

  await step('importing files fills the three slots; a fourth is refused; a published take cannot be deleted until it is taken out of the pack', async () => {
    await pickFile();
    await until("document.querySelectorAll('.tk').length === 2", 'second take', 8000);
    await pickFile();
    await until("document.querySelectorAll('.tk').length === 3", 'third take', 8000);
    await pickFile();
    await until("[...document.querySelectorAll('.toast')].some((t) => t.textContent.includes('3 takes'))", 'the take limit message', 5000);
    assert.equal(await count('.tk'), 3);
    assert.equal((await takesNow('sample_09')).items.length, 3);
    await q("document.querySelector('.tk.pub .tk-x').click()");
    await until("[...document.querySelectorAll('.toast')].some((t) => t.textContent.includes('in the pack'))", 'cannot remove a published take', 4000);
    assert.equal(await count('.tk'), 3);
    await q("document.querySelector('.tk:not(.pub) .tk-x').click()");
    await until("document.querySelectorAll('.tk').length === 2", 'an unpublished take removed', 5000);
    await q("document.querySelector('.tk.pub .tk-b').click()");
    await until("document.querySelector('.unpub')", 'the take-out button');
    await click('.unpub');
    await until("!document.querySelector('.pubdot')", 'taken out of the pack', 6000);
    const l = await lineNow('sample_09');
    assert.deepEqual([l.ownedDub, l.publishedTakeId], [false, null]);
  });

  await step('creating a pack from the picker on the real HTTP adapter opens the Packs page new-pack form, shows the pending pack and the restart message without 409 errors, then switches back', async () => {
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('[data-act=\"new-pack\"]')", 'the picker menu');
    await click('[data-act="new-pack"]');
    await until("document.querySelector('#page-view .m-newpack #np-name')", 'the form');
    await q("document.querySelector('#np-name').value='Smoke Test Pack'");
    await click('[data-act="np-create"]');
    await until("document.querySelector('#banner').classList.contains('b-first') && document.querySelector('#ed')?.readOnly === true", 'first-run destination editor', 8000);
    assert.match(await text('.pick-v'), /Smoke Test Pack/);
    assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), true);
    assert.equal(await q("document.querySelector('#ed').readOnly"), true);
    const created = (await raw("(await r.studioPacks()).packs")).find((p) => p.id === 'smoke-test-pack');
    assert.ok(created && created.restartRequired === true && created.loaded === false, 'created on the fake loader as pending');
    assert.deepEqual(page.problems, [], 'no 409 or console problems when opening the pending pack');
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('.pack-menu')", 'the picker again');
    await click(`.pack-menu .mi[data-id="${PACK}"]`);
    await until("!document.querySelector('#banner').classList.contains('b-first') && document.querySelector('#ed')?.readOnly === false", 'back on the editable pack', 8000);
  });

  await step('Phase B1 & Phase C on the real HTTP adapter: voice filter on Voice lines page, quick Record -> Keep & next, Packs page, Settings page, Export page zip, and New pack with icon', async () => {
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    await click('[data-act="vfilter"][data-id="voiced"]');
    await until("document.querySelector('.vchip[data-id=\"voiced\"]').classList.contains('on')", 'voiced filter on');
    const voicedUi = parseInt(await text('#l-count'), 10);
    await click('[data-act="vfilter"][data-id="unvoiced"]');
    await until("document.querySelector('.vchip[data-id=\"unvoiced\"]').classList.contains('on')", 'unvoiced filter on');
    const unvoicedUi = parseInt(await text('#l-count'), 10);
    assert.equal(voicedUi + unvoicedUi, 40, 'voiced + unvoiced equals all 40 lines on the fake loader');
    await click('[data-act="vfilter"][data-id="all"]');

    await selectRow('sample_09');
    await click('[data-act="record"]');
    await until("document.querySelector('.recbadge')", 'recording on sample_09', 8000);
    await sleep(1400);
    await click('[data-act="record"]');
    await until("document.querySelectorAll('.tk').length === 3", 'third take saved on sample_09', 8000);
    // inherited from pack voice default set earlier in this session on sample_09!
    await key('t');
    await until("document.querySelector('.tk.pub .pubdot')", 'third take published on sample_09', 10000);
    assert.equal((await lineNow('sample_09')).ownedDub, true, 'sample_09 dub published on the fake loader');
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    await click('#page-view [data-act="next-unrecorded"]');
    await until("document.querySelector('.lid') && document.querySelector('.lid').textContent !== 'sample_09'", 'Next unrecorded moved to next unrecorded line in Studio', 8000);

    // Phase C: Packs page + Settings page + Export page + New pack with author/description/icon on the real HTTP adapter
    await click('#top [data-act="page-tab"][data-id="packs"]');
    await until("document.querySelector('#page-view .m-packs')", 'Packs page open');
    assert.ok((await count('.pcard')) >= 2, 'pack cards rendered');

    await click('#top [data-act="page-tab"][data-id="settings"]');
    await until("document.querySelector('#page-view .m-settings .st-voice')", 'Settings page open');
    assert.equal((await raw(`await r.studioVoiceDefault('${PACK}')`)).origin, 'own', 'first take confirmation set pack voice default on fake loader');
    await click('.m-settings [data-act="pack-voice-origin"][data-id="ai-assisted"]');
    await click('.m-settings [data-act="pack-voice-save"]');
    await sleep(300);
    assert.equal((await raw(`await r.studioVoiceDefault('${PACK}')`)).origin, 'ai-assisted', 'pack voice default updated via PUT P/voice on fake loader');

    await click('#top [data-act="page-tab"][data-id="export"]');
    await until("document.querySelector('#page-view .m-export .chk-list')", 'Export checks loaded from fake loader', 8000);
    assert.equal(await count('.chk-row'), 7, 'all 7 checks from fake loader');
    // TD-13: checks arrive as code + params and are worded in the UI language, not the loader's English prose.
    const uiLang = await q("document.documentElement.lang");
    const rows = await q("[...document.querySelectorAll('.chk-row .chk-main span')].map(e => e.textContent).join(' | ')");
    assert.doesNotMatch(rows, /\d+ voiced line\(s\) without a dub, \d+ without a subtitle|No links in the pack\./, `coded checks are worded by the page (${uiLang}): ${rows}`);
    if (uiLang === 'th') assert.match(rows, /ขาดเสียงพากย์|ทุกบรรทัดมีซับ/, 'Thai progress wording');
    await click('[data-act="export-bump"][data-id="minor"]');
    await q("(()=>{const t=document.querySelector('#exp-notes');t.value='Fake loader export test';t.dispatchEvent(new Event('input',{bubbles:true}));})()");
    await click('[data-act="export-run"]');
    await until("document.querySelector('.exp-res')", 'export archive built on fake loader', 8000);
    const expFile = await q("document.querySelector('[data-act=export-download]').dataset.file");
    assert.match(expFile, /^sample-thai-dub-.*\.zip$/);
    assert.match(await text('.exp-res-folder'), /\.maker-exports/, 'TD-5: the result names the folder the zip is saved in');
    const zipHead = await raw(`Array.from(new Uint8Array(await (await r.studioDownloadExport('${PACK}', ${JSON.stringify(expFile)})).arrayBuffer()).slice(0, 4))`);
    assert.deepEqual(zipHead, [0x50, 0x4b, 0x03, 0x04], 'downloaded valid ZIP from fake loader');

    await click('#top [data-act="page-tab"][data-id="packs"]');
    await until("document.querySelector('#page-view .m-packs')", 'Packs page');
    await click('#page-view [data-act="new-pack"]');
    await until("document.querySelector('#page-view .m-newpack #np-name')", 'New pack form');
    await q("(()=>{const f=(s,v)=>{const e=document.querySelector(s);e.value=v;e.dispatchEvent(new Event('input',{bubbles:true}));};f('#np-name','Fake Full Pack');f('#np-id','fake-full-pack');f('#np-author','Arthit');f('#np-desc','Full metadata pack');})()");
    await click('[data-act="np-gen-icon"]');
    await click('[data-act="np-create"]');
    await until("document.querySelector('#banner').classList.contains('b-first') && document.querySelector('.lid')", 'created full pack on fake loader and switched back to Studio', 8000);
    const info = await raw("await r.studioPackInfo('fake-full-pack')");
    assert.deepEqual([info.author, info.description, info.hasIcon], ['Arthit', 'Full metadata pack', true]);
    assert.deepEqual(page.problems, [], 'no console or HTTP errors');
  });

  await step('offline (the network goes away): the banner says so, the typed text stays, and it is saved when the connection is back', async () => {
    await loadStudio(page, url('en', '&reconnect=300'));
    await selectRow('sample_10');
    await page.send('Network.emulateNetworkConditions', { offline: true, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
    await type('typed while offline');
    await until("document.querySelector('#banner').classList.contains('b-off')", 'the offline banner', 12000);
    assert.equal(await q("document.querySelector('#ed').value"), 'typed while offline');
    await page.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
    await until("!document.querySelector('#banner').classList.contains('b-off')", 'back online', 25000);
    await until("document.querySelector('.save') && /^Saved to pack/.test(document.querySelector('.save').textContent)", 'saved after reconnect', 15000);
    assert.equal((await lineNow('sample_10')).ownedSubtitle, 'typed while offline');
  });

  await step('a lost session (the token is refused) ends with a clear banner and no further requests', async () => {
    await q("document.querySelector('meta[name=\"cwyls-maker-token\"]').setAttribute('content','0'.repeat(64))");
    await type('after the session ended');
    await until("document.querySelector('#banner').classList.contains('b-off') && /maker/.test(document.querySelector('#banner').textContent)", 'the session banner', 12000);
    await sleep(400);
    const n = page.requests.length;
    await sleep(1500);
    assert.equal(page.requests.length, n, 'nothing is sent after a 401');
  });

  console.log(`\n${steps} steps ok`);
} finally {
  await chrome.close();
  await fake.close();
}
