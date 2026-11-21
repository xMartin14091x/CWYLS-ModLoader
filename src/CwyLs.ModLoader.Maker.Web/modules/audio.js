import loader from './loader/index.js';
// All imported/recorded clips are converted before any adapter upload.
const clips=new Map(),saved=new Map();
let recorder,stream,ctx,analyser,animation,started,chunks=[];
export let recording=false;
export function register(state){saved.clear();for(const l of state.lines)for(const takes of Object.values(l.takes||{}))for(const take of takes)if(take.file)saved.set(take.id,{packId:state.pack.id,file:take.file});}
export function hasClip(id){return clips.has(id)||saved.has(id);}
export function getClip(id){return clips.get(id);}
export function pcmWav(buffer){
 const samples=buffer.getChannelData(0),bytes=new ArrayBuffer(44+samples.length*2),v=new DataView(bytes);
 const str=(at,s)=>{for(let i=0;i<s.length;i++)v.setUint8(at+i,s.charCodeAt(i));};
 str(0,'RIFF');v.setUint32(4,36+samples.length*2,true);str(8,'WAVE');str(12,'fmt ');v.setUint32(16,16,true);v.setUint16(20,1,true);v.setUint16(22,1,true);v.setUint32(24,48000,true);v.setUint32(28,96000,true);v.setUint16(32,2,true);v.setUint16(34,16,true);str(36,'data');v.setUint32(40,samples.length*2,true);
 samples.forEach((sample,i)=>{const n=Math.max(-1,Math.min(1,sample));v.setInt16(44+i*2,Math.round(n*(n<0?32768:32767)),true);});return new Blob([bytes],{type:'audio/wav'});
}
async function convert(blob,{maxDuration=Infinity}={}){let ac;try{
 ac=new AudioContext();const input=await ac.decodeAudioData(await blob.arrayBuffer());
 if(!Number.isFinite(input.duration)||input.duration<=0)throw new Error('Empty audio');
 if(input.duration>maxDuration)throw new Error(`Your take is too long. Use at most ${maxDuration} seconds.`);
 const off=new OfflineAudioContext(1,Math.ceil(input.duration*48000),48000),mono=off.createBuffer(1,input.length,input.sampleRate),data=mono.getChannelData(0);
 for(let channel=0;channel<input.numberOfChannels;channel++){const c=input.getChannelData(channel);for(let i=0;i<data.length;i++)data[i]+=c[i]/input.numberOfChannels;}
 const source=off.createBufferSource();source.buffer=mono;source.connect(off.destination);source.start();const output=await off.startRendering(),samples=output.getChannelData(0),peaks=[];
 for(let i=0;i<80;i++){let max=0;for(let j=Math.floor(i*samples.length/80);j<Math.floor((i+1)*samples.length/80);j++)max=Math.max(max,Math.abs(samples[j]));peaks.push(max);}
 return {blob:pcmWav(output),duration:output.duration,peaks};
 }catch(error){if(error.message?.startsWith('Your take is too long.'))throw error;throw new Error('This audio file could not be decoded. Try WAV, MP3 or M4A.');}finally{await ac?.close();}}
export async function addFile(file,options){const info=await convert(file,options),id=globalThis.crypto?.randomUUID?.()||`take-${Date.now().toString(36)}-${Math.random().toString(36).slice(2,10)}`;clips.set(id,info.blob);return {id,name:`${id}.wav`,duration:info.duration,peaks:info.peaks,start:0,end:info.duration,used:true};}
export async function saveTake(take,packId,lineId,language){const path=`audio/${lineId}/${language}/${take.id}.wav`;await loader.putFile(packId,path,clips.get(take.id));take.file=path;saved.set(take.id,{packId,file:path});return take;}
export async function start(done,onMeter,onError=()=>{},options) {
 if(!window.isSecureContext)throw new Error('Microphone recording needs localhost or HTTPS. You can still import audio here.');
 if(!navigator.mediaDevices?.getUserMedia||!window.MediaRecorder)throw new Error('This browser cannot record audio. Import an audio file instead.');
 stream=await navigator.mediaDevices.getUserMedia({audio:true});
 try{
 ctx=new AudioContext();await ctx.resume();analyser=ctx.createAnalyser();analyser.fftSize=256;ctx.createMediaStreamSource(stream).connect(analyser);
 const mime=['audio/webm;codecs=opus','audio/mp4','audio/webm'].find(m=>MediaRecorder.isTypeSupported(m));
 recorder=mime?new MediaRecorder(stream,{mimeType:mime}):new MediaRecorder(stream);chunks=[];
 recorder.ondataavailable=e=>{if(e.data.size)chunks.push(e.data);};
 recorder.onstop=async()=>{const blob=new Blob(chunks,{type:recorder.mimeType});cleanup();if(!blob.size){onError(new Error('No audio was recorded. Try again or import a take.'));return;}try{await done(await addFile(blob,options));}catch(e){onError(e);}};
 recorder.start(100);started=performance.now();recording=true;
 const samples=new Uint8Array(analyser.fftSize);function tick(){analyser.getByteTimeDomainData(samples);let sum=0;for(const v of samples)sum+=((v-128)/128)**2;onMeter(Math.min(1,Math.sqrt(sum/samples.length)*3),[...samples].filter((_,i)=>i%4===0).map(v=>Math.abs(v-128)/128));animation=requestAnimationFrame(tick);}tick();
 }catch(e){cleanup();throw e;}
}
function cleanup(){recording=false;cancelAnimationFrame(animation);stream?.getTracks().forEach(t=>t.stop());ctx?.close();}
export function stop(){if(recorder?.state==='recording')recorder.stop();}
let player,stopTimer,playToken=0;
export function stopPlayback(){playToken++;clearTimeout(stopTimer);if(player){player.pause();URL.revokeObjectURL(player.src);player=null;}}
export async function play(take,onEnd=()=>{}){stopPlayback();const token=playToken,ref=saved.get(take.id);let blob=clips.get(take.id);if(!blob&&ref){blob=await loader.getFile(ref.packId,ref.file);clips.set(take.id,blob);}if(token!==playToken)return;if(!blob)throw new Error('This take belongs to an earlier session. Please import or record it again.');player=new Audio(URL.createObjectURL(blob));const p=player;player.onloadedmetadata=()=>{p.currentTime=take.start||0;p.play().catch(onEnd);stopTimer=setTimeout(()=>{stopPlayback();onEnd();},Math.max(.1,(take.end||take.duration)-(take.start||0))*1000);};player.onended=()=>{stopPlayback();onEnd();};}
