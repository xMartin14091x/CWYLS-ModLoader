// Zero-dependency CDP acceptance check. Launch an isolated Chrome on port 9234,
// serve this app on 127.0.0.1:8764, then run node tests/browser.mjs.
import assert from 'node:assert/strict';
import {mkdir,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
const base=process.env.DS_URL||'http://127.0.0.1:8764/';
const http=process.env.DS_HTTP==='1';
const firstId=http?'sample_01':'sample_click_001',editId=http?'sample_02':'sample_click_002';
const shots=process.env.DS_SHOTS||join(tmpdir(),'cwyls-maker-shots');
await mkdir(shots,{recursive:true});
const tab=(await(await fetch('http://127.0.0.1:9234/json/list')).json()).find(t=>t.type==='page');
const ws=new WebSocket(tab.webSocketDebuggerUrl);await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[],requests=[];
ws.addEventListener('message',({data})=>{const m=JSON.parse(data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);if(m.error)p.reject(new Error(JSON.stringify(m.error)));else p.resolve(m.result);}if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.method==='Runtime.consoleAPICalled'&&m.params.type==='error')errors.push(m.params.args);if(m.method==='Network.requestWillBeSent')requests.push(m.params.request.url);});
const cdp=(method,params={})=>new Promise((resolve,reject)=>{pending.set(++id,{resolve,reject});ws.send(JSON.stringify({id,method,params}));});
const evaluate=async(expression)=>{const r=await cdp('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true,userGesture:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function wait(expression){for(let n=0;n<100;n++){if(await evaluate(expression))return;await sleep(100);}throw new Error('Timed out: '+expression);}
const click=selector=>evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
const change=(selector,value,event='change')=>evaluate(`(()=>{const el=document.querySelector(${JSON.stringify(selector)});el.value=${JSON.stringify(value)};el.dispatchEvent(new Event('${event}',{bubbles:true}));})()`);
let checks=0;const check=(condition,message)=>{assert.ok(condition,message);checks++;};
try{
 await cdp('Runtime.enable');await cdp('Page.enable');await cdp('Network.enable');
 await cdp('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
 await cdp('Page.navigate',{url:base});await wait('window.maker?.studio().total > 0');await sleep(650);
 check(await evaluate('window.maker.view === "studio"'),'Studio is default');
 check(await evaluate('document.querySelector("#studio-now").textContent.includes("Practice")')||http,'Now speaking event visible');
 check(await evaluate('window.maker.studio().status.gameLanguage === window.maker.studio().lang ? !document.querySelector("#studio-language-note").textContent.trim() : document.querySelector("#studio-language-note").textContent.includes("Switch the game to")'),'Language mismatch prompt follows game status');
 check(await evaluate(`window.maker.backend === '${http?'http':'mock'}'`),'Adapter mode selected correctly');
 await wait('!!window.maker.studio().now?.localizationId');const spokenId=await evaluate('window.maker.studio().now.localizationId');await click('[data-studio-action="now"]');await wait(`window.maker.studio().lineId === ${JSON.stringify(spokenId)}`);checks++;
 await change('[data-studio-bind="search"]',editId,'input');await wait('window.maker.studio().total === 1');check(await evaluate(`document.querySelector(".studio-line").textContent.includes(${JSON.stringify(editId)})`),'Search id');
 await change('[data-studio-bind="search"]','','input');await wait('window.maker.studio().total > 1');
 await click('[data-studio-action="next"]');await wait('document.querySelector(".studio-pagination").textContent.includes("11–20") && !document.querySelector("[data-studio-action=previous]").disabled');checks++;
 await click('[data-studio-action="previous"]');await wait('document.querySelector(".studio-pagination").textContent.includes("1–10") && !document.querySelector("[data-studio-action=next]").disabled');
 await change('[data-studio-bind="filter"]','missing-subtitle');await wait('!document.querySelector("#dub-studio").getAttribute("aria-busy").includes("true")');check(await evaluate('[...document.querySelectorAll(".studio-line")].every(el=>el.textContent.includes("Missing subtitle"))'),'Missing subtitle filter');
 await change('[data-studio-bind="filter"]','all');await wait('window.maker.studio().total > 30');
 await click(`[data-id="${editId}"]`);await wait(`window.maker.studio().lineId === ${JSON.stringify(editId)}`);
 await change('[data-studio-bind="subtitle"]','ทดสอบดาวกระดาษของฉัน','input');await click(`[data-id="${firstId}"]`);await wait(`window.maker.studio().lineId === ${JSON.stringify(firstId)}`);await click(`[data-id="${editId}"]`);await wait(`window.maker.studio().lineId === ${JSON.stringify(editId)}`);check(await evaluate('document.querySelector("#studio-subtitle").value === "ทดสอบดาวกระดาษของฉัน"'),'Draft survives line navigation');
 await evaluate('import("./modules/loader/index.js").then(({default:loader})=>loader.bus.dispatchEvent(new Event("resync")))');await wait('!document.querySelector("#dub-studio").getAttribute("aria-busy").includes("true")');check(await evaluate('document.querySelector("#studio-subtitle").value === "ทดสอบดาวกระดาษของฉัน"'),'Draft survives event resync');
 await change('[data-studio-bind="subtitle"]','ก'.repeat(150),'input');check(await evaluate('document.querySelector("#studio-length").textContent.includes("Aim for 20")'),'Length warning');
 await change('[data-studio-bind="subtitle"]','ดาวกระดาษดวงเล็กของเรา','input');await click('[data-studio-action="save"]');await wait('!window.maker.studio().dirty && !document.querySelector("#dub-studio").getAttribute("aria-busy").includes("true")');check(await evaluate('document.querySelector("#toasts").textContent.includes("live in game")'),'Save acknowledged live');
 // Import a synthetic creator-owned tone through the actual file input/converter.
 await evaluate(`(()=>{const n=22050,b=new ArrayBuffer(44+n*4),v=new DataView(b);const str=(i,s)=>[...s].forEach((c,j)=>v.setUint8(i+j,c.charCodeAt(0)));str(0,'RIFF');v.setUint32(4,b.byteLength-8,true);str(8,'WAVE');str(12,'fmt ');v.setUint32(16,16,true);v.setUint16(20,1,true);v.setUint16(22,2,true);v.setUint32(24,22050,true);v.setUint32(28,88200,true);v.setUint16(32,4,true);v.setUint16(34,16,true);str(36,'data');v.setUint32(40,n*4,true);for(let i=0;i<n;i++){v.setInt16(44+i*4,Math.sin(i/22)*1000,true);v.setInt16(46+i*4,Math.sin(i/22)*1000,true);}const dt=new DataTransfer();dt.items.add(new File([b],'my-synthetic-tone.wav',{type:'audio/wav'}));const el=document.querySelector('#studio-audio-file');el.files=dt.files;el.dispatchEvent(new Event('change',{bubbles:true}));})()`);
 await wait('!document.querySelector("[data-studio-action=upload]").disabled');check(await evaluate('document.querySelector(".studio-take").textContent.includes("1.00 s")'),'Stereo 22.05 kHz import converted to own WAV take');
 await click('[data-studio-action="listen"]');await sleep(250);check(requests.some(url=>url.startsWith('blob:')),'Own take plays through a local Blob URL');
 await click('[data-studio-action="upload"]');await wait('!document.querySelector("[data-studio-action=dub]").disabled');checks++;
 await click('[data-studio-action="original"]');await wait('window.maker.logs.some(l=>l.topic === "maker.play")');await wait('!document.querySelector("[data-studio-action=dub]").disabled');await click('[data-studio-action="dub"]');await wait(`window.maker.studio().now?.localizationId === ${JSON.stringify(editId)}`);await wait('!document.querySelector("#dub-studio").getAttribute("aria-busy").includes("true")');checks++;
 // Exercise recorder lifecycle without accessing a real microphone.
 await evaluate(`(()=>{window.qaAudioContext=new AudioContext();const dest=qaAudioContext.createMediaStreamDestination(),osc=qaAudioContext.createOscillator();osc.connect(dest);osc.start();Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:async()=>dest.stream});})()`);
 await click('[data-studio-action="record"]');await wait('document.querySelector("[data-studio-action=record]").textContent.includes("Stop")');await sleep(350);await click('[data-studio-action="record"]');await wait('!document.querySelector("[data-studio-action=upload]").disabled');check(await evaluate('!document.querySelector("[data-studio-action=record]").textContent.includes("Stop")'),'Record stops and releases stream');await evaluate('qaAudioContext.close()');
 const views=['studio','pack','lines','looks','translate','scenes','share','wiring','booth','welcome'];
 for(const [name,width,height] of [['desktop',1440,1000],['ipad',768,1024],['phone',390,844]]){
  await cdp('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:width<800});
  await evaluate('document.querySelector("#toasts").replaceChildren()');
  for(const view of views){await evaluate(`location.hash=${JSON.stringify(view)}`);await wait(`window.maker.view === '${view}'`);await sleep(80);check(await evaluate('document.documentElement.scrollWidth <= innerWidth'),`${name} ${view} no horizontal page scroll`);if(http&&view!=='studio')check(await evaluate('document.querySelector(".workspace").textContent.includes("Coming later")'),`${view} HTTP placeholder`);
   if(view==='studio'){await evaluate('document.querySelector(".studio-progress").open=true');check(await evaluate('document.documentElement.scrollWidth <= innerWidth'),`${name} expanded progress fits`);await evaluate('document.querySelector(".studio-progress").open=false');}
   const shot=await cdp('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(`${shots}/${http?'http':'mock'}-${view}-${name}.png`,Buffer.from(shot.data,'base64'));
   if(view==='studio'){await evaluate('document.querySelector(".studio-editor").scrollIntoView({block:"start"})');await sleep(100);const editorShot=await cdp('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(`${shots}/${http?'http':'mock'}-editor-${name}.png`,Buffer.from(editorShot.data,'base64'));await evaluate('document.querySelector(".view-enter").scrollTop=0;scrollTo(0,0)');}
  }
  if(!http){for(const kind of ['line','pack']){const baseView=kind==='pack'?'pack':'lines';await evaluate(`location.hash=${JSON.stringify(baseView)}`);await wait(`window.maker.view === '${baseView}'`);await click(`[data-action="new-${kind}"]`);await wait('!!document.querySelector(".guided-layout")');check(await evaluate('document.documentElement.scrollWidth <= innerWidth'),`${name} guided ${kind} fits`);const shot=await cdp('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(`${shots}/mock-guided-${kind}-${name}.png`,Buffer.from(shot.data,'base64'));await click('[data-action="cancel-guide"]');}}
 }
 await evaluate('location.hash="studio"');await wait('window.maker.view === "studio"');
 check(errors.length===0,'Zero console errors/exceptions: '+JSON.stringify(errors));
 check(requests.every(url=>/^(http:\/\/(127\.0\.0\.1|localhost)(:|\/)|data:|blob:)/.test(url)),'Only loopback/data/blob requests: '+requests.filter(url=>!url.startsWith('http://127.0.0.1')));
 console.log(JSON.stringify({mode:http?'http':'mock',checks,views:views.length,sizes:3,errors,requests:requests.length,shots},null,2));
}finally{ws.close();}
