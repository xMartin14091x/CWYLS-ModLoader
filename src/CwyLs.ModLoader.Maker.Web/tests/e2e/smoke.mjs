// Content-level click-through of the M2 foundation against the dev backend (not just layout: the matrix cannot tell a wrong
// state from a right one). Run bounded:   perl -e 'alarm 150; exec @ARGV' node tests/e2e/smoke.mjs
// Later milestones extend this one script (typing/autosave, takes, publish/play, conflict, offline/reconnect) and add
// `--backend fake` for W2's fake loader.
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { inflateSync } from 'node:zlib';
import { launchChrome, openPage, setViewport, loadStudio, screenshot } from './cdp.mjs';
import { encodeWavPcm16Mono } from '../../ui/audio/wav.js';
import { serveWeb } from './server.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
setTimeout(() => { console.error('WATCHDOG: smoke took too long'); process.exit(2); }, 140000).unref();

const shots = process.argv.includes('--shots') ? process.argv[process.argv.indexOf('--shots') + 1] : null; // optional screenshots of a few states
const server = await serveWeb(ROOT);
// Chrome's fake microphone (a beeping stream, permission granted without a prompt) lets the record flow run headless
const chrome = await launchChrome({ extraArgs: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] });
let steps = 0;
try {
  const page = await openPage(chrome.port);
  // count how often the page asks for the microphone, and with which constraints
  await page.send('Page.addScriptToEvaluateOnNewDocument', { source: "window.__gum = 0; window.__gumArgs = []; window.__streams = []; const orig = navigator.mediaDevices && navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices); if (orig) navigator.mediaDevices.getUserMedia = (c) => { window.__gum++; window.__gumArgs.push(JSON.stringify(c)); return orig(c).then((s) => { window.__streams.push(s); return s; }); };" });
  await setViewport(page, 1440, 900);
  const q = (expr) => page.eval(expr);
  const until = async (expr, what, ms = 5000) => {
    const t0 = Date.now();
    while (Date.now() - t0 < ms) { if (await q(expr)) return; await sleep(50); }
    const where = await q("JSON.stringify({count:(document.querySelector('#l-count')||{}).textContent, lang:document.documentElement.lang, lid:(document.querySelector('.lid')||{}).textContent})").catch(() => '?');
    throw new Error(`timed out waiting for: ${what} (page: ${where})`);
  };
  const step = async (name, fn) => { await fn(); steps++; console.log(`ok  ${String(steps).padStart(2)} ${name}`); };
  const url = (fixture = 'normal', lang = 'th') => `${server.url}index.html?ui=2&backend=dev&fixture=${fixture}&lang=${lang}`;
  const click = (sel) => q(`document.querySelector(${JSON.stringify(sel)}).click()`);
  const text = (sel) => q(`(document.querySelector(${JSON.stringify(sel)})||{}).textContent||''`);
  const count = (sel) => q(`document.querySelectorAll(${JSON.stringify(sel)}).length`);

  await step('the default page is Studio v2 (no ?ui=2); ?ui=1 still opens the v1 page', async () => {
    await page.send('Page.navigate', { url: `${server.url}index.html?ui=1&backend=mock` });
    await until("!!document.querySelector('#app') && document.querySelector('#app').children.length > 0", 'v1 renders into #app with ?ui=1', 10000);
    assert.equal(await count('#studio'), 0, 'v2 shell must not exist when ?ui=1 is requested');
    await loadStudio(page, `${server.url}index.html?backend=dev&fixture=normal&lang=th`);
    assert.equal(await count('#studio'), 1, 'plain URL opens Studio v2 by default');
  });

  await step('v2 loads on the dev backend: top bar with 5 pages + progress bars, scene nav, list, line card, deck', async () => {
    await loadStudio(page, url());
    assert.equal(await q('document.documentElement.lang'), 'th');
    assert.equal(await count('.top-tab'), 5, 'five page tabs in the top bar');
    assert.equal(await count('.scn-nav'), 1, 'compact scene navigator in Studio left pane');
    assert.match(await text('.scn-sub'), /\/44/, 'shows total scenes in navigator');
    assert.equal(await text('.lid'), 'clickheroine_rest_010');
    assert.equal(await q("document.querySelector('#ed').value"), 'ว่าไง? เมื่อกี้เรียกเรารึเปล่า');
    assert.equal(await count('.tk'), 3, 'three take pills');
    assert.equal(await q("document.querySelector('.tk.fav .tk-b').textContent.includes('2')"), true, 'take 2 is the favourite');
    assert.match(await text('.prog-sub .v'), /^1,3\d\d\/2,169$/, 'overall subtitle progress from the groups route, not hard-coded');
    assert.match(await text('.prog-dub .v'), /^\d+\/2,043$/, 'overall dub progress in the top bar');
    assert.equal(await q('window.__studio.conn.api.raw.dev.localization.length'), 2172, 'mixed localization includes three UI strings');
    assert.equal(await q("window.__studio.store.get().lines.some(l => l.id.startsWith('ui_'))"), false, 'Studio never receives UI strings');
    assert.equal(await q('(async () => (await window.__studio.conn.api.raw.studioCapabilities()).uiText)()'), false, 'UI text editing is explicitly unavailable');
  });

  await step('no outside requests, no problems so far', async () => {
    assert.deepEqual(page.requests.filter((u) => !u.startsWith(server.url) && !u.startsWith('data:')), []);
    assert.deepEqual(page.problems, []);
  });

  await step('the UI language toggles to English and back; the choice is only stored when made by hand', async () => {
    await click('[data-act="ui-en"]');
    await until("document.documentElement.lang === 'en'", 'lang en');
    assert.match(await text('.brand-studio'), /Dub & Subtitle Studio/);
    assert.equal(await q("localStorage.getItem('cwyls-maker-ui-lang')"), 'en');
    await click('[data-act="ui-th"]');
    await until("document.documentElement.lang === 'th'", 'lang th');
  });

  await step('Voice lines page: a scene filters the list; "All scenes" clears it', async () => {
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page open');
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='pomodoro';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    await until("/^6\\s/.test(document.querySelector('#l-count').textContent)", 'six pomodoro lines');
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    await until("document.querySelector('#l-count').textContent.includes('2169')", 'all lines');
  });

  await step('status chips use the workflow status; search narrows by text and id', async () => {
    await click('[data-act="filter"][data-id="retake"]');
    await until("/^2\\s/.test(document.querySelector('#l-count').textContent)", 'two lines need a retake');
    await click('[data-act="filter"][data-id="all"]');
    await q("(()=>{const i=document.querySelector('[data-in=q]');i.value='Pomodoro, start';i.dispatchEvent(new Event('input',{bubbles:true}));})()");
    await until("/^1\\s/.test(document.querySelector('#l-count').textContent)", 'one match');
    assert.match(await text('.vb-row .vb-c-sub'), /เริ่มโพโมโดโร่/);
    await q("(()=>{const i=document.querySelector('[data-in=q]');i.value='';i.dispatchEvent(new Event('input',{bubbles:true}));})()");
  });

  await step('selecting a line from Voice lines opens Studio and ArrowDown moves to the next line', async () => {
    await click('[data-act="filter"][data-id="all"]');
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='smalltalk';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    await until("/^7\\s/.test(document.querySelector('#l-count').textContent) && document.querySelector('.vb-row') && document.querySelector('.vb-row').dataset.id === 'smalltalk_tea_001'", 'the smalltalk rows');
    await click('.vb-row');
    await until("document.querySelector('.lid') && document.querySelector('.lid').textContent === 'smalltalk_tea_001'", 'first smalltalk line in Studio');
    await q("document.body.focus(); document.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}))");
    await until("document.querySelector('.lid').textContent === 'smalltalk_tea_002'", 'ArrowDown moved to the next line');
  });

  const raw = (expr) => q(`(async()=>{const r=window.__studio.conn.api.raw;return JSON.stringify(${expr});})()`).then(JSON.parse);
  const type = (value) => q(`(()=>{const e=document.querySelector('#ed');e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('input',{bubbles:true}));})()`);
  const lineNow = () => raw("await r.studioLine(window.__studio.store.get().packId, window.__studio.store.get().packLang, window.__studio.store.get().lineId)");

  await step('typing updates the meters live, then autosaves after the pause: the footer says so only once the loader holds the text', async () => {
    await type('ก'.repeat(40));
    await until("document.querySelector('.save') && document.querySelector('.save').textContent.includes('กำลังบันทึก')", 'the "saving…" note while the debounce runs');
    assert.equal(await count('.cpl-i'), 2, 'the wrap meter follows the typing');
    assert.doesNotMatch(await text('.cfoot'), /บันทึกลงแพ็กแล้ว/, `not claimed while still typing (draft: ${await q("JSON.stringify((({status,text,baseText})=>({status,text:text.length,baseText:baseText.length}))(__studio.store.get().draft))")})`);
    await until("document.querySelector('.save') && document.querySelector('.save').textContent.includes('บันทึกลงแพ็กแล้ว')", 'the saved note', 6000);
    assert.equal((await lineNow()).ownedSubtitle, 'ก'.repeat(40), 'the loader really holds it');
    assert.match(await text('.row.on .row-t'), /^ก{40}$/, 'the list shows what was saved');
  });

  await step('the history menu lists revisions; the pack picker lists packs with reasons', async () => {
    await page.eval("window.__studio.actions.selectLine('clickheroine_rest_010')");
    await until("document.querySelector('.lid').textContent === 'clickheroine_rest_010'", 'back on the brief line');
    await click('[data-act="hist"]');
    await until("document.querySelectorAll('.hm-i').length === 3", 'three history items');
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('.pack-menu')", 'the picker');
    assert.equal(await count('.pack-menu .mi.ro'), 3, 'reference, code and failed packs are listed read-only');
    assert.match(await text('.pack-menu .mi.ro'), /แพ็กอ้างอิง/);
  });

  await step('status, star and needs-retake reach the loader and the screen', async () => {
    await page.eval("window.__studio.actions.selectLine('clickheroine_rest_008')");
    await until("document.querySelector('.lid').textContent === 'clickheroine_rest_008'", 'line');
    await click('[data-act="status"][data-id="draft"]');
    await until("document.querySelector('.seg-b.on.s-draft')", 'draft chip');
    await click('#star-btn');
    await until("document.querySelector('#star-btn.on')", 'starred');
    await click('[data-act="retake"]');
    await until("document.querySelector('.retake.on')", 'retake on');
    await until("true", 'settle', 200);
    await sleep(300);
    const l = await lineNow();
    assert.deepEqual([l.workflow.status, l.workflow.starred], ['needs-retake', true]);
    assert.equal(await count('.row.on .m-flag'), 1, 'the list row carries the retake flag');
    await click('[data-act="retake"]');
    await sleep(300);
    assert.equal((await lineNow()).workflow.status, 'draft', 'switching it off returns to draft');
  });

  await step('history revert puts an older text back into the box and into the loader', async () => {
    await page.eval("window.__studio.actions.selectLine('clickheroine_rest_010')");
    await until("document.querySelector('.lid').textContent === 'clickheroine_rest_010'", 'line');
    await click('[data-act="hist"]');
    await until("document.querySelectorAll('.hm-i').length >= 3", 'history');
    const older = await q("document.querySelectorAll('.hm-i')[2].querySelector('.hm-t').textContent");
    await q("document.querySelectorAll('.hm-i')[2].querySelector('[data-act=revert]').click()");
    await until(`document.querySelector('#ed').value === ${JSON.stringify(older)}`, 'the older text in the box');
    await sleep(500);
    assert.equal(await q("document.querySelector('#ed').value"), older);
    assert.equal((await lineNow()).ownedSubtitle, older);
  });

  await step('a conflict keeps the draft, offers the pack\'s version, and both choices work', async () => {
    const bump = (txt, op) => raw(`await (async()=>{const st=window.__studio.store.get();const l=await r.studioLine(st.packId,st.packLang,st.lineId);return r.studioSaveSubtitle(st.packId,st.packLang,st.lineId,{expectedRevision:l.revision,operationId:'${op}',text:${JSON.stringify(txt)}});})()`);
    await bump('จากแท็บอื่นหนึ่ง', 'f'.repeat(32));
    await type('ของฉันหนึ่ง');
    await until("document.querySelector('.cflt')", 'the conflict row', 6000);
    assert.match(await text('.cflt'), /จากแท็บอื่นหนึ่ง/);
    assert.equal(await q("document.querySelector('#ed').value"), 'ของฉันหนึ่ง', 'the draft is untouched');
    await click('[data-act="conflict-theirs"]');
    await until("!document.querySelector('.cflt') && document.querySelector('#ed').value === 'จากแท็บอื่นหนึ่ง'", 'theirs adopted');
    await bump('จากแท็บอื่นสอง', 'e'.repeat(32));
    await type('ของฉันสอง');
    await until("document.querySelector('.cflt')", 'second conflict', 6000);
    await click('[data-act="conflict-mine"]');
    await until("!document.querySelector('.cflt') && document.querySelector('.save') && document.querySelector('.save').textContent.includes('บันทึกลงแพ็กแล้ว')", 'mine saved', 6000);
    assert.equal((await lineNow()).ownedSubtitle, 'ของฉันสอง');
  });

  await step('Enter in the editor saves and moves to the next line', async () => {
    await page.eval("window.__studio.actions.selectLine('morning_wake_001')");
    await until("document.querySelector('.lid').textContent === 'morning_wake_001'", 'line');
    await type('ก่อนกด Enter');
    await q("document.querySelector('#ed').focus(); document.querySelector('#ed').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))");
    await until("document.querySelector('.lid').textContent === 'morning_wake_002'", 'next line');
    await sleep(400);
    assert.equal((await raw("await r.studioLine('satone-th-dub','th','morning_wake_001')")).ownedSubtitle, 'ก่อนกด Enter', 'saved at once, not lost');
  });

  await step('creating a pack from the picker opens the Packs page new-pack form, shows the pending pack and the restart message', async () => {
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('[data-act=\"new-pack\"]')", 'the picker menu');
    await click('[data-act="new-pack"]');
    await until("document.querySelector('#page-view .m-newpack #np-name')", 'the form');
    // Type like a user: the form keeps its fields in state from input events, so a later repaint keeps the name.
    await q("(() => { const i = document.querySelector('#np-name'); i.value = 'Smoke Test Pack'; i.dispatchEvent(new Event('input', { bubbles: true })); })()");
    await click('[data-act="np-create"]');
    await until("document.querySelector('#banner').classList.contains('b-first')", 'first-run banner', 6000);
    assert.match(await text('.pick-v'), /Smoke Test Pack/);
    assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), true);
    assert.equal(await q("document.querySelector('#ed').readOnly"), true);
  });

  await step('game status arrives as events: calm top pill for paused/background without a banner; the UI language follows the game', async () => {
    await q("localStorage.removeItem('cwyls-maker-ui-lang')");
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev`);
    assert.equal(await q('document.documentElement.lang'), 'th', 'follows the game language (th)');
    // selectLine kicks off dub.hydrate() without awaiting it; wait for all take waveforms and their paint before snapshotting DOM refs.
    await until("(() => { const s = window.__studio?.store?.get(); return !!s?.line && (s.line.takes || []).slice(0, 3).every((t) => !t.sourcePresent || !!s.peaks?.[t.id]) && document.querySelectorAll('#wave .wsvg rect').length > 0; })()", 'deck waveforms hydrated', 10000);
    await q("new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))");
    await q("window.__statusRefs = { logo: document.querySelector('#top .logo'), star: document.querySelector('#stars').firstElementChild, hero: document.querySelector('#hero').firstElementChild, deck: document.querySelector('#deck').firstElementChild, vlist: document.querySelector('#vlist') }");
    await q("window.__studio.conn.api.raw.devControls.setStatus({paused:true, focused:false})");
    await until("document.querySelector('.link--paused')", 'paused top pill');
    assert.equal(await q("document.querySelector('#banner').hidden"), true, 'no banner when paused');
    assert.match(await text('.link--paused'), /เกมหยุดอยู่/);
    await q("window.__studio.conn.api.raw.devControls.setStatus({paused:false, focused:false})");
    await until("/เกมอยู่เบื้องหลัง/.test((document.querySelector('.link--paused')||{}).textContent||'')", 'background top pill');
    assert.equal(await q("document.querySelector('#banner').hidden"), true, 'no banner when in background');
    await q("window.__studio.conn.api.raw.devControls.setStatus({paused:false, focused:true})");
    await until("document.querySelector('.link--ok')", 'back to ok');
    assert.equal(await q("window.__statusRefs.logo === document.querySelector('#top .logo') && window.__statusRefs.star === document.querySelector('#stars').firstElementChild && window.__statusRefs.hero === document.querySelector('#hero').firstElementChild && window.__statusRefs.deck === document.querySelector('#deck').firstElementChild && window.__statusRefs.vlist === document.querySelector('#vlist')"), true, 'status updates do not rebuild topbar shell, sky, hero, deck or vlist');
    await q("window.__studio.conn.api.raw.devControls.setStatus({gameLanguage:'en'})");
    await until("document.documentElement.lang === 'en'", 'UI follows the game to English');
    await q("window.__studio.conn.api.raw.devControls.setStatus({gameLanguage:'th'})");
    await until("document.documentElement.lang === 'th'", 'and back');
  });

  await step('offline: the page says so and keeps the typed text; when the game is back the text is saved', async () => {
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=en&reconnect=150`);
    await q("window.__studio.conn.api.raw.devControls.setOffline(true)");
    await type('typed while offline');
    await until("document.querySelector('#banner').classList.contains('b-off')", 'offline banner', 8000);
    assert.equal(await q("document.querySelector('#ed').value"), 'typed while offline');
    assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), true);
    await q("window.__studio.conn.api.raw.devControls.setOffline(false)");
    await until("document.querySelector('#banner').hidden", 'back online', 15000);
    await until("document.querySelector('.save') && /Saved to pack/.test(document.querySelector('.save').textContent)", 'saved after reconnect', 8000);
    assert.equal((await lineNow()).ownedSubtitle, 'typed while offline');
  });

  await step('a lost session (401) ends with a clear banner and no further requests', async () => {
    await q("window.__studio.conn.api.raw.devControls.fail('status','unauthorized',{times:1})");
    await q("window.__studio.actions.refreshStatus()");
    await until("document.querySelector('#banner').classList.contains('b-off') && /maker/.test(document.querySelector('#banner').textContent)", 'session banner');
    const calls = await q("window.__studio.conn.api.raw.devControls.log.length");
    await sleep(800);
    assert.equal(await q("window.__studio.conn.api.raw.devControls.log.length"), calls, 'nothing is sent after a 401');
  });

  // ---------------- M4: the dub deck and Play (one page session: the dev backend lives as long as the page) ----------------
  const takesNow = (id) => raw(`await r.studioTakes('satone-th-dub','th','${id}')`);
  const key = (k, shift = false) => q(`document.body.dispatchEvent(new KeyboardEvent('keydown',{key:${JSON.stringify(k)},shiftKey:${shift},bubbles:true,cancelable:true}))`);
  const wavPath = join(tmpdir(), 'cwyls-smoke-take.wav');
  writeFileSync(wavPath, encodeWavPcm16Mono(Float32Array.from({ length: 44100 }, (_, i) => 0.3 * Math.sin(2 * Math.PI * 220 * i / 44100)), 44100));
  const pickFile = async () => {
    await page.send('DOM.enable');
    const { root } = await page.send('DOM.getDocument');
    const { nodeId } = await page.send('DOM.querySelector', { nodeId: root.nodeId, selector: '#file' });
    await page.send('DOM.setFileInputFiles', { files: [wavPath], nodeId });
  };

  await step('the microphone is asked for only on the click, with echo cancellation, noise suppression and auto gain OFF; the halo follows the input', async () => {
    await loadStudio(page, url('nomic', 'en'));
    assert.equal(await q('window.__gum'), 0, 'not asked at load');
    await click('[data-act="setup-mic"]');
    await until("document.querySelector('.recwrap:not(.off)')", 'mic ready', 10000);
    assert.equal(await q('window.__gum'), 1);
    const c = JSON.parse(await q('window.__gumArgs[0]'));
    assert.deepEqual([c.audio.echoCancellation, c.audio.noiseSuppression, c.audio.autoGainControl], [false, false, false]);
    await until("parseFloat(document.querySelector('.recwrap') && document.querySelector('.recwrap').style.getPropertyValue('--lvl') || '0') > 0.05", 'a level on the halo (the fake microphone beeps)', 8000);
    assert.ok((await count('.dev select option')) >= 1, 'a device picker');
  });

  await step('R records after a silent 3-2-1; the take is saved with its audio and shows a waveform of the creator\'s own voice', async () => {
    await page.eval("window.__studio.actions.selectLine('morning_wake_006')");
    await until("document.querySelector('.lid').textContent === 'morning_wake_006'", 'empty line');
    await key('r');
    await until("document.querySelector('.count')", 'the count-in', 4000);
    assert.equal((await takesNow('morning_wake_006')).items.length, 0, 'nothing exists during the count-in');
    await until("document.querySelector('.recbadge')", 'recording', 8000);
    await q("window.__recBtn = document.querySelector('.recbtn'); window.__recBox = window.__recBtn.getBoundingClientRect().toJSON()");
    for (const lv of ['idle', 'noise', 'clip', 'good']) {
      await q(`window.__studio.store.set({ level: '${lv}', lvl: 0.7 })`);
      await sleep(120);
      const ok = await q("(()=>{const b=document.querySelector('.recbtn'), r=b.getBoundingClientRect(), r0=window.__recBox; return b===window.__recBtn && Math.abs(r.top-r0.top)<0.5 && Math.abs(r.left-r0.left)<0.5 && Math.abs(r.width-r0.width)<0.5 && Math.abs(r.height-r0.height)<0.5;})()");
      assert.equal(ok, true, `record button stayed in place and was not recreated on level=${lv}`);
    }
    await sleep(1300);
    await key('r');
    await until("document.querySelectorAll('.tk').length === 1", 'the take pill', 8000);
    const t = (await takesNow('morning_wake_006')).items[0];
    assert.deepEqual([t.origin, t.consent.confirmed, t.sourcePresent], ['own', false, true]);
    assert.ok(t.sourceDurationSec > 1.2 && t.sourceDurationSec < 3.5, String(t.sourceDurationSec));
    await until("document.querySelectorAll('.wave .wsvg rect').length >= 10", 'a waveform', 5000);
    assert.equal(await q("document.querySelector('.recwrap').classList.contains('off')"), true, 'microphone is released as soon as recording finishes');
    assert.deepEqual(await q('window.__streams.map((s) => s.getTracks().every((t) => t.readyState === "ended"))'), [true], 'the browser stopped using the microphone when recording finished');
  });

  await step('the consent statement is shown in full, and only its button confirms; the take records that', async () => {
    await click('[data-ch="consent"]');
    await until("document.querySelector('.consent-menu')", 'the statement');
    assert.equal(await q("document.querySelector('[data-ch=consent]').checked"), false, 'ticking alone does not confirm');
    assert.equal(await text('.consent-menu .cm-t'), "I confirm this take is my own voice, or I have the speaker's permission to use it in this pack, and the origin I picked is true. I will not present an AI voice as the original voice actor.");
    await click('[data-act="consent-confirm"]');
    await until("document.querySelector('[data-ch=consent]').checked", 'ticked');
    const t = (await takesNow('morning_wake_006')).items[0];
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
    assert.equal((await takesNow('morning_wake_006')).items[0].trimStartSec, 0, 'nothing sent while dragging');
    await q("document.getElementById('deck').dispatchEvent(new PointerEvent('pointerup',{bubbles:true,pointerId:7}))");
    await until("true", 'x', 100);
    await sleep(500);
    assert.ok((await takesNow('morning_wake_006')).items[0].trimStartSec > 0.2, 'sent on release');
    await click('[data-ch="even"]');
    await sleep(400);
    assert.equal((await takesNow('morning_wake_006')).items[0].evenOutVolume, true);
  });

  await step('T plays my version in W2\'s order (render, publish, play); Shift+T plays the original; the game is asked once each', async () => {
    await q('window.__studio.conn.api.raw.devControls.log.length = 0');
    await key('t');
    await until("document.querySelector('.hb-mine.busy')", 'the game is speaking', 10000);
    const order = await raw("r.devControls.log.map((l) => l.method).filter((m) => ['saveSubtitle','putTakeAudio','publish','play'].includes(m))");
    assert.deepEqual(order, ['putTakeAudio', 'publish', 'play']);
    const l = await lineNow();
    assert.deepEqual([l.ownedDub, l.publishedTakeId === (await takesNow('morning_wake_006')).items[0].id], [true, true]);
    await until("document.querySelector('.said-t') && /ไม่|[ก-๙]/.test(document.querySelector('.said-t').textContent) || document.querySelector('.said-m')", 'Satone just said', 6000);
    await q('window.__studio.conn.api.raw.devControls.log.length = 0');
    await sleep(3200);
    await key('T', true);
    await until("document.querySelector('.hb-orig.busy')", 'the original is speaking', 10000);
    const plays = await raw("r.devControls.log.filter((l) => l.method === 'play').map((l) => l.args[1])");
    assert.deepEqual(plays, ['original']);
  });

  await step('Space plays the take locally (the game is not involved) and stops it again', async () => {
    await q('window.__studio.conn.api.raw.devControls.log.length = 0');
    await key(' ');
    await until("document.querySelector('.playtake.on')", 'local playback', 5000);
    await key(' ');
    await until("!document.querySelector('.playtake.on')", 'stopped');
    assert.equal(await raw("r.devControls.log.filter((l) => l.method === 'play').length"), 0);
  });

  await step('importing files fills the three slots; a fourth is refused with a message; removing a take frees a slot; a published take is taken out of the pack first', async () => {
    await pickFile();
    await until("document.querySelectorAll('.tk').length === 2", 'second take', 8000);
    await pickFile();
    await until("document.querySelectorAll('.tk').length === 3", 'third take', 8000);
    await pickFile();
    await until("[...document.querySelectorAll('.toast')].some((t) => t.textContent.includes('3 takes'))", 'the take limit message', 5000);
    assert.equal(await count('.tk'), 3);
    assert.equal((await takesNow('morning_wake_006')).items.length, 3);
    const pubPill = await q("document.querySelector('.tk.pub .tk-x').disabled === false && !!document.querySelector('.tk.pub .pubdot')");
    assert.equal(pubPill, true, 'the published take has a marker');
    await q("document.querySelector('.tk.pub .tk-x').click()");
    await until("[...document.querySelectorAll('.toast')].some((t) => t.textContent.includes('in the pack'))", 'cannot remove a published take', 4000);
    assert.equal(await count('.tk'), 3);
    await q("document.querySelector('.tk:not(.pub) .tk-x').click()");
    await until("document.querySelectorAll('.tk').length === 2", 'a take removed', 5000);
    await q("document.querySelector('.tk.pub .tk-b').click()");
    await until("document.querySelector('.unpub')", 'the take-out button');
    await click('.unpub');
    await until("!document.querySelector('.pubdot')", 'taken out of the pack', 5000);
    assert.equal((await lineNow()).ownedDub, false);
  });

  // ---------------- M4b: the microphone is let go when idle or hidden; Play after a successful publish ----------------
  const ended = () => q('window.__streams.map((s) => s.getTracks().every((t) => t.readyState === "ended"))');
  const hidePage = (hidden) => q(`(()=>{Object.defineProperty(document,'visibilityState',{get:()=>${hidden ? "'hidden'" : "'visible'"},configurable:true});document.dispatchEvent(new Event('visibilitychange'));})()`);

  await step('an unused microphone is let go after the idle time: the tracks end, the halo says it is off, and nothing is asked again', async () => {
    await loadStudio(page, `${url('nomic', 'en')}&micidle=1500`);
    await click('[data-act="setup-mic"]');
    await until("document.querySelector('.recwrap:not(.off)')", 'mic ready', 10000);
    assert.deepEqual(await ended(), [false], 'the stream is live while the mic is on');
    await until("document.querySelector('.recwrap.off') && /Mic is off/.test((document.querySelector('.setup-n')||{}).textContent||'')", 'the quiet "mic is off" state', 6000);
    assert.deepEqual(await ended(), [true], 'the browser really stopped using the microphone');
    assert.equal(await q('window.__gum'), 1, 'releasing never asks for anything');
    assert.equal(await q("document.querySelector('[data-act=setup-mic]').disabled"), false, 'Set up is there to wake it');
  });

  await step('R wakes the microphone without a new prompt and starts the count-in; Esc cancels and releases the microphone immediately', async () => {
    await page.eval("window.__studio.actions.selectLine('morning_wake_006')");
    await until("document.querySelector('.lid').textContent === 'morning_wake_006'", 'a line with no takes (the brief line already holds three)');
    await key('r');
    await until("document.querySelector('.count')", 'the count-in after waking', 6000);
    assert.equal(await q('window.__gum'), 2, 'one new stream, same permission');
    assert.equal(JSON.parse(await q('window.__gumArgs[1]')).audio.echoCancellation, false, 'same raw-audio settings on the second stream');
    await sleep(1800);
    assert.equal(await count('.count'), 1, 'the idle clock does not fire during a count-in (it is longer than the idle time here)');
    await key('Escape');
    await until("!document.querySelector('.count') && document.querySelector('.recwrap.off')", 'released immediately on cancel', 4000);
    assert.deepEqual(await ended(), [true, true]);
  });

  await step('a hidden page lets the microphone go at once; Set up takes it again', async () => {
    await click('[data-act="setup-mic"]');
    await until("document.querySelector('.recwrap:not(.off)')", 'mic ready again', 8000);
    assert.equal(await q('window.__gum'), 3);
    await hidePage(true);
    await until("document.querySelector('.recwrap.off')", 'released when hidden', 3000);
    assert.deepEqual((await ended()).slice(-1), [true]);
    await hidePage(false);
    await click('[data-act="setup-mic"]');
    await until("document.querySelector('.recwrap:not(.off)')", 'woken by Set up', 8000);
    assert.equal(await q('window.__gum'), 4);
  });

  await step('Play after a successful publish: if the game cannot play it, the message says the take is already in the pack', async () => {
    await loadStudio(page, url('normal', 'en'));
    assert.equal(await q("document.querySelector('.tk.pub') !== null"), true, 'this line already has a published take');
    await q("window.__studio.conn.api.raw.devControls.setStatus({paused:true, focused:false})");
    await until("document.querySelector('.link--paused')", 'paused');
    await key('t');
    await until("[...document.querySelectorAll('.toast')].some((t) => t.textContent.includes('In the pack. Click the game window, then press T to hear it.'))", 'the in-the-pack message', 8000);
    assert.equal(await q("document.querySelector('.tk.pub') !== null"), true, 'the take is still in the pack');
  });

  // ---------------- M5b: the missing-subtitle / missing-dub filters and "another pack wins" notes ----------------
  await step('the No subtitle and No dub chips list exactly the lines the loader\'s own missing-subtitle and missing-dub filters return', async () => {
    await loadStudio(page, url('normal', 'en'));
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    for (const [chip, wire] of [['nosub', 'missing-subtitle'], ['nodub', 'missing-dub']]) {
      const loader = await raw(`(await r.studioLines({packId:'satone-th-dub',lang:'th',filter:'${wire}',limit:1})).total`);
      await click(`[data-act="filter"][data-id="${chip}"]`);
      await until(`document.querySelector('.chip-${chip}.on')`, `${chip} chip on`);
      await sleep(250);
      const ui = parseInt(await text('#l-count'), 10);
      assert.equal(ui, loader, `chip ${chip}: UI ${ui}, loader ${loader}`);
      assert.ok(loader > 0 && loader < 2169, 'a real subset');
    }
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='night';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    await sleep(250);
    const rows = await q("[...document.querySelectorAll('.vb-row')].map((r) => r.dataset.id)");
    assert.ok(rows.length > 0 && rows.every((id) => id.startsWith('night_')), 'the scene narrows the dub filter too');
    await q("(()=>{const s=document.querySelector('[data-ch=scnfilter]');s.value='';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    await click('[data-act="filter"][data-id="all"]');
    await click('#top [data-act="page-tab"][data-id="studio"]');
    await until("document.querySelector('.lid')", 'back on Studio');
  });

  await step('another pack\'s subtitle or dub is named: "only" when this pack has none, "wins" when ours loses; the note goes when our text takes over', async () => {
    const rowsNow = () => count('.srcrow');
    await page.eval("window.__studio.actions.selectLine('clickheroine_rest_010')");
    await until("document.querySelector('.lid').textContent === 'clickheroine_rest_010'", 'plain line');
    assert.equal(await rowsNow(), 0, 'nothing to say when this pack is what the game uses');
    await page.eval("window.__studio.actions.selectLine('night_goodnight_002')");
    await until("document.querySelector('.lid').textContent === 'night_goodnight_002'", 'line with another pack');
    await until("document.querySelectorAll('.srcrow').length === 2", 'subtitle and dub notes', 4000);
    assert.match(await text('#srcnote'), /Thai Subs \(community\)/);
    assert.match(await text('#srcnote'), /This pack has no subtitle here/);
    assert.match(await text('#srcnote'), /This pack has no dub here/);
    await type('ซับของฉันเอง');
    await until("document.querySelectorAll('.srcrow').length === 1", 'the subtitle note goes after our text is saved and the loader says who wins', 7000);
    assert.match(await text('#srcnote'), /dub/i);
    await page.eval("window.__studio.actions.selectLine('story01_013')");
    await until("document.querySelector('.lid').textContent === 'story01_013'", 'subtitle outranked');
    await until("/not yours/.test(document.querySelector('#srcnote').textContent)", 'wins note');
    assert.match(await text('#srcnote'), /Yours stays saved in this pack/);
    await page.eval("window.__studio.actions.selectLine('story01_011')");
    await until("document.querySelector('.lid').textContent === 'story01_011'", 'dub outranked');
    await until("/plays the dub from/.test(document.querySelector('#srcnote').textContent)", 'dub wins note');
    await click('[data-act="ui-th"]');
    await until("/แพ็ก/.test(document.querySelector('#srcnote').textContent)", 'the note in Thai');
    await click('[data-act="ui-en"]');
  });

  // ---------------- M5c: Open line for the line the game just spoke, and the game-language notice ----------------
  await step('Open line: the line the game just spoke is brought up, and a scene, filter or search that hid it is cleared', async () => {
    await loadStudio(page, url('normal', 'en'));
    const emit = (id) => q(`window.__studio.conn.api.raw.devControls.emit('line.spoken', {voice:'v', text:'', textJa:'', language:'th', localizationId:${JSON.stringify(id)}, source:'ambient', scenarioGroupId:'night', durationSec:2})`);
    await page.eval("window.__studio.store.set({ view: { ...window.__studio.store.get().view, group: 'night', filter: 'done', q: 'zzz' } });");
    await emit('smalltalk_tea_002');
    await until("document.querySelector('.said-open')", 'the Open line button', 4000);
    assert.equal(await q("document.querySelector('.said-open').dataset.id"), 'smalltalk_tea_002');
    assert.equal(await count('.row[data-id="smalltalk_tea_002"]'), 0, 'the line is hidden by the current view');
    await click('.said-open');
    await until("document.querySelector('.lid').textContent === 'smalltalk_tea_002'", 'the spoken line is open');
    await until("document.querySelector('.row.on') && document.querySelector('.row.on').dataset.id === 'smalltalk_tea_002'", 'and visible in the list');
    assert.equal(await q("window.__studio.store.get().view.filter"), 'all', 'the filter was cleared');
    assert.equal(await q("window.__studio.store.get().view.q"), '', 'and the search');
    assert.ok((await count('.toast')) >= 1, 'and the creator was told');
    await emit('smalltalk_tea_002');
    await until("/open line/i.test(document.querySelector('.said').textContent) && !document.querySelector('.said-open')", 'the open line needs no button');
    await emit('not_a_line_in_this_list');
    await until("/not in this list/.test(document.querySelector('.said').textContent) && !document.querySelector('.said-open')", 'a line outside the list is named, not offered');
    if (shots) await screenshot(page, `${shots}/dev-en-said.png`);
  });

  await step('the game runs in another language than the pack: a note in the mirror (and a badge at the pack language), a Switch button only when the pack has that language', async () => {
    await loadStudio(page, url('normal', 'en'));
    assert.equal(await count('.mir-lang'), 0, 'no note while the languages agree');
    assert.equal(await count('.langwarn'), 0);
    await q("window.__studio.conn.api.raw.devControls.setStatus({gameLanguage:'ja'})");
    await until("document.querySelector('.mir-lang')", 'the note', 4000);
    assert.match(await text('.mir-lang'), /The game is in Japanese, this pack is in Thai/);
    assert.equal(await count('.mir-lang [data-act="match-lang"]'), 0, 'this pack has no Japanese');
    assert.equal(await count('.langwarn'), 1);
    assert.equal(await count('.mir-screen'), 0, 'How it shows in game panel is removed');
    if (shots) await screenshot(page, `${shots}/dev-en-langmismatch.png`);
    await q("window.__studio.conn.api.raw.devControls.setStatus({gameLanguage:'th'})");
    await until("!document.querySelector('.mir-lang') && !document.querySelector('.langwarn')", 'the note goes when the languages agree again', 4000);
    // a pack that has both languages: Switch edits the game's language
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=en&packlangs=th,en`);
    await q("window.__studio.conn.api.raw.devControls.setStatus({gameLanguage:'en'})");
    await until("document.querySelector('.mir-lang [data-act=match-lang]')", 'the Switch button', 4000);
    assert.match(await text('.mir-lang [data-act=match-lang]'), /Switch to English/);
    await click('.mir-lang [data-act="match-lang"]');
    await until("document.querySelector('[data-ch=packlang]').value === 'en' && !document.querySelector('.mir-lang') && !document.querySelector('.langwarn')", 'the pack language follows and the note is gone', 8000);
    await click('[data-act="ui-th"]');
    await q("window.__studio.conn.api.raw.devControls.setStatus({gameLanguage:'th'})");
    await until("/ภาษา/.test((document.querySelector('.mir-lang')||{}).textContent||'')", 'the note in Thai', 4000);
    await click('[data-act="ui-en"]');
  });

  // ---------------- Phase B1 & Phase C: voice/character browser, quick record -> keep & next, Packs / New pack / Export ----------------
  await step('Phase B1: voice/unvoiced and character filters on Voice lines page, Next unrecorded line, and quick Record -> Keep & next flow', async () => {
    await loadStudio(page, url('normal', 'en'));
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page')", 'Voice lines page');
    const allCount = parseInt(await text('#l-count'), 10);
    await click('[data-act="vfilter"][data-id="voiced"]');
    await until("document.querySelector('.vchip[data-id=\"voiced\"]').classList.contains('on')", 'voiced filter on');
    const voicedCount = parseInt(await text('#l-count'), 10);
    await click('[data-act="vfilter"][data-id="unvoiced"]');
    await until("document.querySelector('.vchip[data-id=\"unvoiced\"]').classList.contains('on')", 'unvoiced filter on');
    const unvoicedCount = parseInt(await text('#l-count'), 10);
    assert.ok(voicedCount > 0 && unvoicedCount > 0 && voicedCount + unvoicedCount === allCount);
    await click('[data-act="vfilter"][data-id="all"]');
    await q("(()=>{const s=document.querySelector('[data-ch=charfilter]');s.value='satone';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
    await until(`parseInt(document.querySelector('#l-count').textContent, 10) === ${voicedCount}`, 'Satone speaker filter');
    await q("(()=>{const s=document.querySelector('[data-ch=charfilter]');s.value='all';s.dispatchEvent(new Event('change',{bubbles:true}));})()");

    await page.eval("window.__studio.actions.selectLine('morning_wake_001')");
    await click('#page-view [data-act="next-unrecorded"]');
    await until("document.querySelector('.lid') && document.querySelector('.lid').textContent === 'morning_wake_004'", 'jumped to first unrecorded voiced line in Studio');

    // Quick record -> stop -> Keep & next (confirms consent once, publishes, and advances to morning_wake_005)
    await page.eval("window.__studio.actions.quickRecord()");
    await until("document.querySelector('.recbadge')", 'recording started via quick-record', 8000);
    await sleep(1400);
    await page.eval("window.__studio.actions.quickRecord()");
    await until("document.querySelectorAll('.tk').length === 1", 'take saved', 8000);
    await page.eval("window.__studio.actions.keepAndNext()");
    await until("document.querySelector('.consent-menu')", 'consent menu opened on first Keep & next');
    await click('[data-act="consent-confirm"]');
    await until("document.querySelector('.lid').textContent === 'morning_wake_005'", 'published and advanced to next unrecorded line', 8000);
    assert.equal((await raw("await r.studioLine('satone-th-dub','th','morning_wake_004')")).ownedDub, true);
  });

  await step('Phase B2: dynamic status flow (To do -> Draft on type, Ctrl+Enter / Enter marks Done & jumps to next unfinished in scene, Settings page)', async () => {
    await page.eval("window.__studio.actions.selectLine('night_goodnight_002')");
    await until("document.querySelector('.lid').textContent === 'night_goodnight_002'", 'on night_goodnight_002');
    assert.equal(await count('.seg-b.on.s-todo'), 1, 'starts as To do');
    assert.equal(await count('.seg-b.s-done .seg-k'), 1, 'Done chip shows shortcut');

    // 1. Typing a subtitle moves To do -> Draft automatically
    await type('ราตรีสวัสดิ์นะ');
    await until("document.querySelector('.seg-b.on.s-draft')", 'auto-transitioned to Draft');
    await sleep(300);
    assert.equal((await raw("await r.studioLine('satone-th-dub','th','night_goodnight_002')")).workflow.status, 'draft');

    // 2. Ctrl+Enter inside editor marks Done and jumps to next unfinished line in same scene
    await q("document.querySelector('#ed').focus(); document.querySelector('#ed').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}))");
    await until("document.querySelector('.lid').textContent !== 'night_goodnight_002' && document.querySelector('.lid').textContent.startsWith('night_')", 'jumped to next unfinished line in night scene');
    await sleep(300);
    assert.equal((await raw("await r.studioLine('satone-th-dub','th','night_goodnight_002')")).workflow.status, 'done');

    // 3. Settings page in top bar toggles auto-Done on move and persists to localStorage
    await click('#top [data-act="page-tab"][data-id="settings"]');
    await until("document.querySelector('#page-view .m-settings')", 'Settings page open');
    assert.equal(await q("document.querySelector('[data-ch=\"set-auto-done\"]').checked"), false, 'default off');
    assert.equal(await q("document.querySelector('[data-ch=\"set-jump-done\"]').checked"), true, 'default on');
    await click('[data-ch="set-auto-done"]');
    const savedSettings = JSON.parse(await q("localStorage.getItem('cwyls-maker-settings')"));
    assert.equal(savedSettings.autoDoneOnNext, true, 'persisted to localStorage');
    await click('#top [data-act="page-tab"][data-id="studio"]');
    await until("document.querySelector('.lid')", 'back on Studio');

    // Moving to next line with autoDoneOnNext on marks the line Done automatically
    const curLineId = await text('.lid');
    await q("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}))");
    await until(`document.querySelector('.lid').textContent !== ${JSON.stringify(curLineId)}`, 'moved to next line');
    await sleep(300);
    assert.equal((await raw(`await r.studioLine('satone-th-dub','th',${JSON.stringify(curLineId)})`)).workflow.status, 'done');
    await q("localStorage.removeItem('cwyls-maker-settings')");
    await page.eval("window.__studio.actions.setSetting('autoDoneOnNext', false)");
  });

  await step('Phase B1 (contract v2.4/v2.5/v2.5.1): Settings page pack voice origin, inherited take ("From pack default" + Override), and bulk apply (followPackDefault:true)', async () => {
    await click('#top [data-act="page-tab"][data-id="settings"]');
    await until("document.querySelector('#page-view .m-settings .st-voice')", 'pack voice section on Settings page');
    await click('.m-settings [data-act="pack-voice-origin"][data-id="ai-assisted"]');
    await click('.m-settings [data-act="pack-voice-save"]');
    await until("document.querySelector('.m-settings [data-act=\"pack-voice-clear\"]')", 'pack voice confirmed');
    assert.equal((await raw("await r.studioVoiceDefault('satone-th-dub')")).origin, 'ai-assisted');
    await click('#top [data-act="page-tab"][data-id="studio"]');
    await until("document.querySelector('.lid')", 'back on Studio');

    await page.eval("window.__studio.actions.selectLine('morning_wake_005')");
    await until("document.querySelector('.lid').textContent === 'morning_wake_005'", 'on morning_wake_005');
    await pickFile();
    await until("document.querySelectorAll('.tk').length === 1 && document.querySelector('.pv-inherited')", 'inherited take pill and From pack default badge', 8000);
    assert.equal(await count('.d-meta [data-ch="consent"]'), 0, 'per-take consent checkbox hidden when inherited from pack default');
    assert.match(await text('.pv-inherited'), /From pack default/);
    const inheritedTake = (await takesNow('morning_wake_005')).items[0];
    assert.deepEqual([inheritedTake.origin, inheritedTake.consent.confirmed, inheritedTake.consentSource], ['ai-assisted', true, 'pack']);

    // Pick a per-take override, confirm it, then explicitly save it.
    assert.equal(await q("document.querySelector('.d-meta [data-act=origin][data-id=\"ai-assisted\"]').getAttribute('aria-checked')"), 'true');
    await click('.d-meta [data-act="origin"][data-id="ai-generated"]');
    await until("document.querySelector('[data-ch=take-origin-consent]')", 'draft controls');
    assert.equal((await takesNow('morning_wake_005')).items[0].consentSource, 'pack', 'draft does not change saved origin');
    await click('[data-ch="take-origin-consent"]');
    await until("document.querySelector('.consent-menu')", 'read consent');
    await click('[data-act="consent-confirm"]');
    await until("!document.querySelector('[data-act=take-origin-save]').disabled", 'save enabled');
    await click('[data-act="take-origin-save"]');
    await until("!document.querySelector('[data-act=take-origin-save]')", 'override saved');
    const overriddenTake = (await takesNow('morning_wake_005')).items[0];
    assert.deepEqual([overriddenTake.origin, overriddenTake.consent.confirmed, overriddenTake.consentSource], ['ai-generated', true, 'take']);
    // Withdrawing the saved consent still makes the take eligible for bulk confirmation.
    await click('[data-ch="consent"]');
    await until("!document.querySelector('[data-ch=consent]').checked", 'consent withdrawn');

    await click('#top [data-act="page-tab"][data-id="settings"]');
    await until("document.querySelector('.m-settings [data-act=\"apply-pack-voice\"]:not([disabled])')", 'bulk apply button enabled on Settings page');
    assert.match(await text('.m-settings [data-act="apply-pack-voice"]'), /\(1\)/, 'shows count of unconfirmed takes');
    await click('.m-settings [data-act="apply-pack-voice"]');
    await until("document.querySelector('.m-settings [data-act=\"apply-pack-voice\"]').disabled", 'unconfirmed count drops to 0 after bulk apply');
    const bulkAppliedTake = (await takesNow('morning_wake_005')).items[0];
    assert.deepEqual([bulkAppliedTake.origin, bulkAppliedTake.consent.confirmed, bulkAppliedTake.consentSource], ['ai-assisted', true, 'pack']);
    await click('#top [data-act="page-tab"][data-id="studio"]');
    await until("document.querySelector('.lid')", 'back on Studio');
  });

  await step('Phase C: Packs page, Export page checks + version bump + release notes + zip download, and New pack form with custom language/id/author/icon', async () => {
    await click('#top [data-act="page-tab"][data-id="packs"]');
    await until("document.querySelector('#page-view .m-packs')", 'Packs page open');
    assert.ok((await count('.pcard')) >= 4, 'lists all packs as cards');
    assert.equal(await count('.packs-wins-note'), 1, 'shows pack-wins info note');
    // Pack info loads after the page opens: wait for it instead of reading the card at once.
    await until("/Creator \\(TH\\)/.test(document.querySelector('.pcard.cur')?.textContent || '')", 'shows author from studioPackInfo');

    await click('#top [data-act="page-tab"][data-id="export"]');
    await until("document.querySelector('#page-view .m-export .chk-list')", 'Export checks loaded');
    assert.equal(await count('.chk-row'), 7, 'all 7 export checks shown');
    await click('[data-act="export-bump"][data-id="minor"]');
    await until("document.querySelector('.bump-b[data-id=\"minor\"]').classList.contains('on')", 'minor bump selected');
    await q("(()=>{const t=document.querySelector('#exp-notes');t.value='Release 0.4.0 notes';t.dispatchEvent(new Event('input',{bubbles:true}));})()");
    await click('[data-act="export-run"]');
    await until("document.querySelector('.exp-res')", 'export result card shown', 6000);
    assert.match(await text('.exp-res'), /satone-th-dub-v0\.4\.0\.zip/);
    assert.equal(await q("window.__studio.store.get().lastDownloadedZip?.magic"), 'PK', 'valid zip downloaded');

    await click('#top [data-act="page-tab"][data-id="packs"]');
    await until("document.querySelector('#page-view .m-packs')", 'Packs page');
    await click('#page-view [data-act="new-pack"]');
    await until("document.querySelector('#page-view .m-newpack #np-name')", 'New pack form open');
    assert.equal(await count('.np-restart'), 1, 'explains the one restart required');
    assert.equal(await count('.np-lang-why'), 1, 'explains why built-in languages are not offered');
    await q("(()=>{const f=(s,v)=>{const e=document.querySelector(s);e.value=v;e.dispatchEvent(new Event('input',{bubbles:true}));};f('#np-name','Cozy Evening Pack');f('#np-id','cozy-evening-pack');f('#np-author','Nicha');f('#np-desc','Evening dub pack');})()");
    await click('[data-act="np-gen-icon"]');
    await click('[data-act="np-create"]');
    await until("document.querySelector('#banner').classList.contains('b-first') && document.querySelector('.lid')", 'pack created and switched back to Studio', 6000);
    const info = await raw("await r.studioPackInfo('cozy-evening-pack')");
    assert.deepEqual([info.author, info.description, info.hasIcon], ['Nicha', 'Evening dub pack', true]);
  });

  await step('Task 6 look-and-feel: custom dropdowns (ARIA listbox + keyboard + data-motion="pop"), aligned status pills, readable merged groups, per-page "?" help & guided tour', async () => {
    await q("localStorage.removeItem('cwyls-maker-tours-seen'); localStorage.removeItem('cwyls-maker-guide-hint')");
    await loadStudio(page, url('normal', 'en'));
    // D-126: the first visit only hints at ?, and the spotlight starts from its help panel.
    assert.equal(await q("document.querySelector('#tour-layer').hidden"), true, 'no automatic tour');
    assert.equal(await count('.guide-hint'), 1, 'one first-visit hint');
    await click('[data-act="keys-menu"]');
    await until("document.querySelector('.help-menu')", 'help open before starting guide');
    await click('[data-act="tour-start"]');
    await until("!document.querySelector('#tour-layer').hidden && document.querySelector('#tour-card')", 'guide starts on request');
    assert.equal(await count('#tour-spotlight'), 1);
    assert.equal(await q("document.querySelector('#tour-card').dataset.motion"), 'pop');
    assert.match(await text('.tour-step-badge'), /1 of 5/);
    await click('[data-act="tour-next"]');
    await until("/2 of 5/.test(document.querySelector('.tour-step-badge').textContent)", 'moved to step 2');
    await click('[data-act="tour-prev"]');
    await until("/1 of 5/.test(document.querySelector('.tour-step-badge').textContent)", 'moved back to step 1');
    await key('Escape');
    await until("document.querySelector('#tour-layer').hidden", 'Escape closed the tour');

    // 2. Per-page "?" Help panel with How to use, How other pages connect, shortcuts, and Start guided tour
    await click('[data-act="keys-menu"]');
    await until("document.querySelector('.help-menu')", 'help popover open');
    assert.equal(await q("document.querySelector('.help-menu').dataset.motion"), 'pop');
    assert.ok((await count('.help-steps li')) >= 3, '3-5 how-to steps');
    assert.equal(await count('.help-connect'), 1, 'how other pages connect');
    assert.equal(await count('[data-act="tour-start"]'), 1, 'Start guided tour button');
    await click('[data-help-tab="glossary"]');
    await until("document.querySelector('.help-glossary')", 'page glossary open');
    assert.ok((await count('.help-term')) >= 3, 'page terms are explained');
    await click('[data-help-tab="guide"]');
    await until("document.querySelector('.help-steps')", 'guide tab restored');
    await click('[data-act="tour-start"]');
    await until("!document.querySelector('#tour-layer').hidden && !document.querySelector('.help-menu')", 'Start guided tour opened tour and closed help panel');
    await click('[data-act="tour-skip"]');
    await until("document.querySelector('#tour-layer').hidden", 'Skip closed the tour');

    // 3. Readable merged display groups in Studio left navigator (clickheroine_rest_008..013 -> Click reactions (rest))
    assert.equal(await text('.scn-title'), 'Click reactions (rest)');

    // 4. Page changes do not start a tour; dropdowns and aligned status pills keep working.
    await click('#top [data-act="page-tab"][data-id="lines"]');
    await until("document.querySelector('#page-view .vb-page') && document.querySelector('#tour-layer').hidden", 'Voice lines opens without a tour');

    // Custom dropdown open/close, keyboard navigation, and selection
    await click('[data-dd-btn="dd-scnfilter"]');
    await until("document.querySelector('[data-dd=\"dd-scnfilter\"]').classList.contains('open') && !document.querySelector('#dd-scnfilter-list').hidden", 'custom group dropdown open');
    assert.equal(await q("document.querySelector('#dd-scnfilter-list').dataset.motion"), 'pop');
    await q("document.querySelector('[data-dd-btn=\"dd-scnfilter\"]').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}))");
    await q("document.querySelector('[data-dd-btn=\"dd-scnfilter\"]').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))");
    await until("!document.querySelector('[data-dd=\"dd-scnfilter\"]').classList.contains('open')", 'Enter selected option and closed dropdown');
    // Reset scene filter via custom dropdown click
    await click('[data-dd-btn="dd-scnfilter"]');
    await click('#dd-scnfilter-opt-0');
    await until("document.querySelector('#l-count').textContent.includes('2169')", 'all lines restored via custom dropdown');

    // Verify all Voice lines status pills have identical width and identical left x coordinate
    const pillStats = await q(`(() => {
      const pills = [...document.querySelectorAll('.vb-row .vb-st')].slice(0, 20);
      const rects = pills.map((p) => p.getBoundingClientRect());
      const w0 = rects[0]?.width || 0, x0 = rects[0]?.left || 0;
      return { count: rects.length, sameW: rects.every((r) => Math.abs(r.width - w0) < 0.5), sameX: rects.every((r) => Math.abs(r.left - x0) < 0.5) };
    })()`);
    assert.ok(pillStats.count >= 10 && pillStats.sameW && pillStats.sameX, `status pills aligned: ${JSON.stringify(pillStats)}`);

    // 5. Settings opens page help; Start remains an explicit choice inside the panel.
    await click('#top [data-act="page-tab"][data-id="settings"]');
    await until("document.querySelector('#page-view .m-settings [data-act=\"reset-tours\"]')", 'Settings Open page help button');
    await click('[data-act="reset-tours"]');
    await until("document.querySelector('.help-menu')?.dataset.page==='settings'", 'Settings help is open');
    assert.equal(await q("document.querySelector('#tour-layer').hidden"), true, 'Settings never starts a tour automatically');
    await click('[data-act="tour-start"]');
    await until("!document.querySelector('#tour-layer').hidden", 'explicit Settings tour');
    await click('[data-act="tour-skip"]');
    await until("document.querySelector('#tour-layer').hidden", 'tour closed');
  });

  await step('a loader without dubs: a clear banner, no microphone prompt, Play and the deck disabled', async () => {
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=en&fixture=nomic&features=catalog,workflow,subtitles,projects`);
    assert.equal(await q("document.querySelector('#banner').classList.contains('b-feat')"), true);
    assert.match(await text('#banner'), /Dub takes are not available/);
    assert.equal(await q("document.querySelector('[data-act=setup-mic]').disabled"), true);
    assert.equal(await q("document.querySelector('[data-act=play-orig]').disabled"), true);
    assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), true);
    assert.equal(await q('window.__gum'), 0);
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=en&features=catalog,subtitles,workflow,dubs,playback`);
    assert.match(await text('#banner'), /History is not available/);
    await click('[data-act="pack-menu"]');
    await until("document.querySelector('.mi-new')", 'the picker');
    assert.equal(await q("document.querySelector('.mi-new').disabled"), true);
    await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=en&fixture=nomic&features=catalog`);
    assert.match(await text('#banner'), /read-only/);
    assert.equal(await q("document.querySelector('#ed').readOnly"), true);
  });

  for (const [fixture, check] of [
    ['paused', async () => { assert.equal(await q("document.querySelector('#banner').hidden"), true); assert.equal(await count('.link--paused'), 1); assert.match(await text('.link--paused'), /เกมหยุดอยู่/); }],
    ['readonly', async () => { assert.equal(await q("document.querySelector('#banner').classList.contains('b-ro')"), true); assert.equal(await q("document.querySelector('#ed').readOnly"), true); assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), true); }],
    ['firstrun', async () => { assert.equal(await q("document.querySelector('#banner').classList.contains('b-first')"), true); assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), true); }],
    ['offline', async () => { assert.equal(await q("document.querySelector('#banner').classList.contains('b-off')"), true); assert.equal(await count('.link--off'), 1); assert.equal(await q("document.querySelector('[data-act=play-mine]').disabled"), true); }],
    ['nomic', async () => { assert.equal(await count('[data-act="setup-mic"]'), 1); }],
    ['countdown', async () => { assert.equal(await count('.count'), 1); }],
    ['recording', async () => { assert.equal(await count('.recbadge'), 1); }],
    ['qa', async () => { assert.ok((await count('.pill.warn')) >= 2, 'the Pomodoro line has several QA pills'); }],
    ['conflict', async () => { assert.equal(await count('.cflt'), 1); assert.equal(await q("document.querySelector('#ed').value"), 'ฉบับของฉันที่พิมพ์ค้างไว้'); }],
    ['unfocused', async () => { assert.equal(await q("document.querySelector('#banner').hidden"), true); assert.equal(await count('.link--paused'), 1); assert.match(await text('.link--paused'), /เกมอยู่เบื้องหลัง/); }],
    ['packs', async () => { assert.equal(await count('#page-view .m-packs .pcard'), 5); }],
    ['newpack', async () => { assert.equal(await count('#page-view .m-newpack #np-name'), 1); }],
    ['export', async () => { assert.equal(await count('#page-view .m-export .chk-row'), 7); }],
    ['settings', async () => { assert.equal(await count('#page-view .m-settings .st-voice'), 1); }]
  ]) {
    await step(`fixture ${fixture}: the right state is on screen`, async () => { await loadStudio(page, url(fixture)); await check(); });
  }

  await step('switching from Studio to another page stays dark (no white flash in light color-scheme)', async () => {
    const editorBrightness = (pngBuf, rect) => {
      let pos = 8, w = 0, h = 0, bpp = 4; const idat = [];
      while (pos < pngBuf.length) {
        const len = pngBuf.readUInt32BE(pos), type = pngBuf.toString('ascii', pos + 4, pos + 8), data = pngBuf.subarray(pos + 8, pos + 8 + len);
        if (type === 'IHDR') { w = data.readUInt32BE(0); h = data.readUInt32BE(4); bpp = data[9] === 6 ? 4 : 3; }
        else if (type === 'IDAT') idat.push(data);
        else if (type === 'IEND') break;
        pos += 12 + len;
      }
      const raw = inflateSync(Buffer.concat(idat)), stride = w * bpp, px = Buffer.alloc(h * stride);
      const paeth = (a, b, c) => { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; };
      for (let y = 0, off = 0; y < h; y++) {
        const f = raw[off++], row = y * stride, prev = (y - 1) * stride;
        for (let x = 0; x < stride; x++) {
          const v = raw[off++], a = x >= bpp ? px[row + x - bpp] : 0, b = y > 0 ? px[prev + x] : 0, c = (x >= bpp && y > 0) ? px[prev + x - bpp] : 0;
          px[row + x] = (f === 1 ? v + a : f === 2 ? v + b : f === 3 ? v + ((a + b) >> 1) : f === 4 ? v + paeth(a, b, c) : v) & 0xff;
        }
      }
      const x0 = Math.max(0, Math.floor(rect.left)), y0 = Math.max(0, Math.floor(rect.top));
      const x1 = Math.min(w, Math.ceil(rect.right)), y1 = Math.min(h, Math.ceil(rect.bottom));
      let sum = 0, white = 0, n = 0;
      for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) {
        const i = (y * w + x) * bpp, r = px[i], g = px[i + 1], b = px[i + 2];
        sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (r >= 245 && g >= 245 && b >= 245) white++;
        n++;
      }
      return { avgLuma: n ? sum / n : 0, whitePixels: white };
    };
    await page.send('Emulation.setEmulatedMedia', { media: 'screen', features: [{ name: 'prefers-color-scheme', value: 'light' }] });
    try {
      await loadStudio(page, url('normal', 'th'));
      assert.match(await q("getComputedStyle(document.documentElement).colorScheme"), /dark/);
      const edRect = await q("(() => { const r = document.getElementById('editor').getBoundingClientRect(); return { left: r.left, top: r.top, right: r.right, bottom: r.bottom }; })()");
      await click('#top [data-act="page-tab"][data-id="lines"]');
      for (let i = 0; i < 3; i++) {
        const shot = await page.send('Page.captureScreenshot', { format: 'png' }, 15000);
        const stats = editorBrightness(Buffer.from(shot.data, 'base64'), edRect);
        assert.ok(stats.avgLuma < 100, `editor area flashed bright during switch (avgLuma=${stats.avgLuma.toFixed(1)})`);
        assert.ok(stats.whitePixels < 10000, `editor area had white flash pixels during switch (whitePixels=${stats.whitePixels})`);
        await sleep(30);
      }
      await until("document.querySelector('#page-view .vb-page') && !document.querySelector('.motion-out-layer')", 'Voice lines switch settled');
    } finally {
      await page.send('Emulation.setEmulatedMedia', { media: '', features: [] });
    }
  });

  await step('a loader without Studio v2 shows the "too old" screen, never mock data', async () => {
    // the real adapter does not exist yet (W3-09): asking for HTTP without a token must not fall back to the dev backend
    await loadStudio(page, `${server.url}index.html?ui=2&backend=http`);
    assert.equal(await q("document.querySelector('#banner').classList.contains('b-off')"), true);
    assert.match(await text('#banner'), /v2/);
    assert.equal(await count('.row'), 0, 'no lines from anywhere');
  });
  console.log(`\n${steps} steps ok`);
} finally {
  await chrome.close();
  await server.close();
}
