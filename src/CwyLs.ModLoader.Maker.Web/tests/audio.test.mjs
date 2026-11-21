import test from 'node:test';
import assert from 'node:assert/strict';
// Only the mock adapter is loaded; these pure converter checks need no browser or device.
globalThis.document={querySelector:()=>null};globalThis.location={search:'?backend=mock'};
const audio=await import('../modules/audio.js');
test('creator take encoder emits interoperable PCM16 mono WAV with clipped samples',async()=>{
 const wav=audio.pcmWav({getChannelData:()=>new Float32Array([-2,0,2])}),bytes=await wav.arrayBuffer(),v=new DataView(bytes),tag=offset=>String.fromCharCode(...new Uint8Array(bytes,offset,4));
 assert.equal(wav.type,'audio/wav');assert.equal(tag(0),'RIFF');assert.equal(tag(8),'WAVE');assert.equal(tag(36),'data');assert.equal(v.getUint16(20,true),1);assert.equal(v.getUint16(22,true),1);assert.equal(v.getUint32(24,true),48000);assert.equal(v.getUint16(34,true),16);assert.equal(v.getUint32(40,true),6);assert.equal(v.getInt16(44,true),-32768);assert.equal(v.getInt16(48,true),32767);
});
test('Studio duration cap rejects long takes before allocating a resampling buffer',async()=>{
 let closed=false;globalThis.AudioContext=class {async decodeAudioData(){return {duration:61};}async close(){closed=true;}};
 globalThis.OfflineAudioContext=class {constructor(){assert.fail('A long take must not allocate the resampler');}};
 await assert.rejects(()=>audio.addFile(new Blob(['synthetic input']),{maxDuration:60}),/at most 60 seconds/);assert.equal(closed,true);
});
