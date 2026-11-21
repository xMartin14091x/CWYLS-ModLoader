import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio, setViewport, screenshot } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';
import { startFakeLoader } from '../e2e/fake-loader.mjs';
import { warningHelp } from '../../ui/logic/warning-help.js';
import { makeT } from '../../ui/i18n/i18n.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
let server, fake, chrome, page;
before(async () => {
  server = await serveWeb(root); fake = await startFakeLoader();
  chrome = await launchChrome({ extraArgs: ['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1'] });
  page = await openPage(chrome.port);
});
after(async () => { page?.close(); await chrome?.close(); await server?.close(); await fake?.close(); });
const q = expr => page.eval(expr);
const frames = () => q('new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))');
const wait = async expr => {
  for (let i = 0; i < 120; i++) { if (await q(expr)) return; await new Promise(r => setTimeout(r, 50)); }
  throw new Error(`Timed out: ${expr}; problems: ${JSON.stringify(page.problems)}`);
};
const dev = async () => {
  await setViewport(page, 1440, 900);
  await loadStudio(page, `${server.url}index.html?backend=dev&lang=en&notour=1`);
  await q("(() => {const s=__studio.store.get(); __studio.store.set({lines:s.lines.map(l=>l.id===s.lineId?{...l,text:'Hello  world'}:l),warnVersion:s.warnVersion+1});})()");
  await frames();
};
const key = async (key, code, modifiers = 0) => {
  await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key, code, modifiers });
  await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key, code, modifiers });
};
const moveTo = async selector => {
  const point = await q(`(() => { const r = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}; })()`);
  await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point }); return point;
};
const clickAt = async point => {
  await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
  await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
};

test('inspector owns the existing controls in working order; native shortcuts retain their action routes', async () => {
  await dev();
  assert.deepEqual(await q(`['.d-mic','.d-tape','.d-meta','#cfoot','#hero'].map(s=>document.querySelector(s).closest('#studio-inspector')?.id)`), Array(5).fill('studio-inspector'));
  const tops = await q(`['.d-mic','.d-tape','.d-meta','#cfoot','#hero'].map(s=>document.querySelector(s).getBoundingClientRect().top)`);
  assert.deepEqual(tops, [...tops].sort((a,b)=>a-b));
  assert.equal(await q("document.querySelector('#studio-card #deck, #studio-card #cfoot, #studio-card #hero')"), null);
  await q(`(() => { window.shortcutCalls=[]; for (const name of ['toggleRecord','playTake','playMine','playOriginal','completeLine']) __studio.actions[name]=()=>shortcutCalls.push(name); document.activeElement?.blur(); })()`);
  for (const k of [['r','KeyR'],[' ','Space'],['t','KeyT'],['T','KeyT',8],['Enter','Enter']]) await key(...k);
  assert.deepEqual(await q('shortcutCalls'), ['toggleRecord','playTake','playMine','playOriginal','completeLine']);
  await q("document.querySelector('#ed').focus()");
  await key('r','KeyR'); await key(' ','Space'); await key('t','KeyT');
  assert.equal(await q('shortcutCalls.length'), 5, 'typing in the subtitle does not trigger Studio actions');
  await q("document.querySelector('[data-act=page-tab][data-id=lines]').click()");
  await wait("document.querySelector('#studio-inspector').hidden");
  assert.equal(await q("document.querySelector('#studio-card').hidden"), true);
});

test('each QA warning has a localized explanation and one concrete next step', () => {
  for (const lang of ['en','th']) for (const issue of [{k:'ph',x:'{name}'},{k:'tag',x:'<b>'},{k:'mai'},{k:'double'},{k:'lines',n:3},{k:'fast',v:'20.2'}]) {
    const help = warningHelp([issue], makeT(lang));
    assert.ok(help.includes(lang === 'en' ? 'Fix:' : 'วิธีแก้:'), help);
    assert.doesNotMatch(help, /warning_fix_|qa_/);
    if (issue.x) assert.ok(help.includes(issue.x), help);
  }
});

test('late creator waveform hydration preserves the deck and its focused controls', async () => {
  await dev();
  await q(`(() => { window.deckBefore=document.querySelector('#deck-body'); window.focusBefore=document.querySelector('.recbtn'); focusBefore.focus(); const s=__studio.store.get(),id=document.querySelector('.handle').dataset.take; __studio.store.set({peaks:{...s.peaks,[id]:Array(96).fill(.7)}}); })()`);
  await frames();
  assert.equal(await q("deckBefore===document.querySelector('#deck-body') && focusBefore===document.activeElement"), true);
  assert.equal(await q("document.querySelectorAll('#wave .wsvg rect').length"), 96);
});

test('three-take inspector, device dropdown, long history and consent fit; small windows can scroll', async () => {
  for (const lang of ['en','th']) for (const [width,height] of [[1440,900],[1180,820]]) {
    await setViewport(page,width,height);
    await loadStudio(page, `${server.url}index.html?backend=dev&lang=${lang}&notour=1`);
    await frames();
    assert.equal(await q("document.querySelectorAll('.tk').length"), 3);
    const within = async selector => {
      const b=await q(`(() => {const a=document.querySelector('#studio-inspector').getBoundingClientRect(),r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {fits:r.left>=a.left && r.right<=a.right && r.top>=a.top && r.bottom<=a.bottom,top:r.top,bottom:r.bottom,left:r.left,right:r.right};})()`);
      assert.ok(b.fits, `${lang} ${width} ${selector}: ${JSON.stringify(b)}`);
    };
    await within('#cfoot'); await within('#hero');
    await q("document.querySelector('[data-dd-btn=dd-device]').click()");
    await wait("document.querySelector('#dd-device-list').classList.contains('is-open')"); await within('#dd-device-list');
    await key('Escape','Escape');
    await q(`__studio.store.set({menu:'hist',history:{items:Array.from({length:20},(_,i)=>({id:String(i),text:'Revision '+i,at:Date.now()-i*60000}))}})`);
    await wait("document.querySelectorAll('.hm-i').length===20 && !!document.querySelector('.hist-menu.is-open')"); await within('.hist-menu');
    await q("document.querySelector('#meters .help-trigger').click()");
    await key('Escape','Escape');
    assert.equal(await q('__studio.store.get().menu'), 'hist', 'Escape closes only the help popover');
    await key('Escape','Escape'); await wait("!document.querySelector('.hist-menu')");
    await q("__studio.store.set({menu:'consent'})");
    await wait("!!document.querySelector('.consent-menu.is-open')"); await within('.consent-menu');
  }
  for (const [width,height] of [[1024,700],[900,600]]) {
    await setViewport(page,width,height); await loadStudio(page, `${server.url}index.html?backend=dev&lang=th&notour=1`); await frames();
    assert.equal(await q("getComputedStyle(document.body).overflow"), 'auto');
    assert.ok(await q("document.querySelector('#studio-inspector').scrollHeight>=document.querySelector('#studio-inspector').clientHeight"));
    await q("document.querySelector('[data-act=play-mine]').scrollIntoView({block:'center',inline:'center'})");
    assert.equal(await q("(() => {const r=document.querySelector('[data-act=play-mine]').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight;})()"), true);
  }
});

for (const [name, selector] of [['warning', '.row-warning'], ['info', '#meters .help-trigger']]) {
  test(`${name} popover: hover and focus show; click and tap pin; Escape returns focus without another action`, async () => {
    await dev();
    const line = await q('__studio.store.get().lineId');
    const point = await moveTo(selector);
    await wait("!document.querySelector('#studio-help').hidden");
    assert.equal(await q(`document.querySelector(${JSON.stringify(selector)}).getAttribute('aria-describedby')`), 'studio-help');
    assert.equal(await q("document.querySelector('#studio-help').getAttribute('role')"), 'tooltip');
    if (name === 'warning') assert.match(await q("document.querySelector('#studio-help').textContent"), /Fix:/);
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 });
    await wait("document.querySelector('#studio-help').hidden");
    await q(`document.querySelector(${JSON.stringify(selector)}).focus()`);
    await wait("!document.querySelector('#studio-help').hidden");
    await clickAt(point);
    await wait("document.querySelector('#studio-help').classList.contains('is-pinned')");
    await page.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 });
    await q('document.activeElement.blur()'); await frames();
    assert.equal(await q("document.querySelector('#studio-help').hidden"), false, 'pin survives loss of hover/focus');
    await key('Escape','Escape');
    assert.equal(await q("document.querySelector('#studio-help').hidden"), true);
    assert.equal(await q(`document.activeElement === document.querySelector(${JSON.stringify(selector)})`), true);
    assert.equal(await q('__studio.store.get().lineId'), line, 'opening a warning does not select its row');
    // Chrome's touch input generates the same click path used on an iPad.
    await page.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point] });
    await page.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await wait("!document.querySelector('#studio-help').hidden && document.querySelector('#studio-help').classList.contains('is-pinned')");
    await clickAt({x:5,y:5});
    assert.equal(await q("document.querySelector('#studio-help').hidden"), true, 'outside click closes');
  });
}

test('all info buttons expose their text; stale virtualized help closes; content stays text-only', async () => {
  await dev();
  const selectors = await q("[...document.querySelectorAll('.help-trigger:not(.m-warn)')].map(n => n.closest('#meters') ? '#meters .help-trigger' : n.closest('.d-meta') ? '.d-meta .help-trigger' : '.tape-ctl .help-trigger')");
  assert.equal(selectors.length, 3);
  for (const selector of selectors) {
    await q(`document.querySelector(${JSON.stringify(selector)}).click()`);
    assert.equal(await q("document.querySelector('#studio-help').textContent"), await q(`document.querySelector(${JSON.stringify(selector)}).dataset.help`));
    await key('Escape','Escape');
  }
  await q("document.querySelector('.row-warning').click()");
  await q("document.querySelector('.row-warning').remove()");
  await wait("document.querySelector('#studio-help').hidden");
  await q(`(() => { const b=document.querySelector('#meters .help-trigger'); b.dataset.help='<img src=x onerror=alert(1)>'; b.click(); })()`);
  assert.equal(await q("document.querySelector('#studio-help').children.length"), 0);
});

test('real page on fake HTTP loader fits both sizes, with and without takes, in EN and TH', async () => {
  let seededTake = false;
  for (const lang of ['en','th']) for (const [width,height] of [[1440,900],[1180,820]]) for (const hasTake of [false,true]) {
    await setViewport(page,width,height);
    await loadStudio(page, `${fake.url}index.html?backend=http&lang=${lang}&notour=1`);
    // Use a fixed, wrapping invented quote through the HTTP playback/event route.
    // A background ticker picked different quote lengths under release-suite load.
    const spoken = await fetch(`${fake.base}/v1/maker/play`, {method:'POST',
      headers:{Authorization:`Bearer ${fake.token}`,'Content-Type':'application/json'},
      body:JSON.stringify({id:'sample_34',variant:'original',lang:'th'})});
    assert.equal(spoken.status, 200);
    await wait("!!document.querySelector('#hero .said-open[data-id=sample_34]')");
    await q("document.querySelector('.row[data-id=sample_02]').click()");
    await wait("document.querySelector('.lid').textContent==='sample_02'");
    if (hasTake) {
      await q("document.querySelector('.row[data-id=sample_01]').click()");
      await wait("document.querySelector('.lid').textContent==='sample_01'");
      if (!seededTake) {
        // A synthetic one-second creator tone, imported through the actual file control.
        await q(`(() => { const n=48000,b=new ArrayBuffer(44+n*2),v=new DataView(b);const str=(i,s)=>[...s].forEach((c,j)=>v.setUint8(i+j,c.charCodeAt(0)));str(0,'RIFF');v.setUint32(4,b.byteLength-8,true);str(8,'WAVE');str(12,'fmt ');v.setUint32(16,16,true);v.setUint16(20,1,true);v.setUint16(22,1,true);v.setUint32(24,48000,true);v.setUint32(28,96000,true);v.setUint16(32,2,true);v.setUint16(34,16,true);str(36,'data');v.setUint32(40,n*2,true);for(let i=0;i<n;i++)v.setInt16(44+i*2,Math.sin(i/22)*(8000+16000*Math.sin(i/8000)**2),true);const d=new DataTransfer();d.items.add(new File([b],'synthetic-tone.wav',{type:'audio/wav'}));const e=document.querySelector('#file');e.files=d.files;e.dispatchEvent(new Event('change',{bubbles:true})); })()`);
        await wait("document.querySelectorAll('.tk').length===1");
        await wait("!!document.querySelector('.consent-menu [data-act=consent-confirm]')");
        await q("document.querySelector('.consent-menu [data-act=consent-confirm]').click()");
        await wait("!document.querySelector('.consent-menu') && !!document.querySelector('[data-ch=consent]:checked')");
        seededTake = true;
      }
      await wait("document.querySelectorAll('.tk').length===1");
      await wait("document.querySelectorAll('#wave .wsvg rect').length===96");
    }
    await q('document.fonts.ready');
    await frames();
    await wait("!!document.querySelector('#hero .said')");
    assert.ok(await q("(() => {const n=document.querySelector('.said-t');return n.getBoundingClientRect().height>parseFloat(getComputedStyle(n).lineHeight)*1.5;})()"), 'spoken fixture exercises a wrapped quote');
    await wait("!document.querySelector('#studio-toasts .toast')");
    const geometry = await q(`(() => {
      const box=n=>{const r=n.getBoundingClientRect();return {left:r.left,top:r.top,right:r.right,bottom:r.bottom,width:r.width,height:r.height};};
      const inspector=document.querySelector('#studio-inspector'), card=document.querySelector('#studio-card');
      const buttons=[...inspector.querySelectorAll('button')].filter(n=>n.getClientRects().length);
      return {inspector:box(inspector),card:box(card),editor:box(document.querySelector('#editor')),scrollWidth:document.documentElement.scrollWidth, buttons:buttons.map(n=>({label:n.textContent, ...box(n)})),overflow:inspector.scrollHeight-inspector.clientHeight}; })()`);
    assert.ok(geometry.card.right <= geometry.inspector.left, JSON.stringify(geometry));
    assert.ok(geometry.editor.height > 200, 'subtitle editor gets the freed height');
    assert.ok(geometry.scrollWidth <= width, JSON.stringify(geometry));
    assert.ok(geometry.overflow <= 1, `${lang} ${width} ${hasTake}: inspector must fit without scrolling: ${JSON.stringify(geometry)}`);
    for (const b of geometry.buttons) assert.ok(b.left >= geometry.inspector.left && b.right <= geometry.inspector.right && b.bottom <= geometry.inspector.bottom && b.top >= geometry.inspector.top, JSON.stringify(b));
    assert.deepEqual(page.problems, []);
    assert.deepEqual(page.requests.filter(u=>!u.startsWith(fake.base) && !/^(blob:|data:)/.test(u)), []);
    if (process.env.INSPECTOR_SHOTS) await screenshot(page, join(process.env.INSPECTOR_SHOTS, `${lang}-${width}x${height}-${hasTake?'with-take':'no-take'}.png`));
  }
});
