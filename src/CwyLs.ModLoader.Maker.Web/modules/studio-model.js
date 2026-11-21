// Catalogue metadata only. A missing voice cannot have a dub; count it separately.
export function progress(lines){
 const groups=new Map();
 const count=items=>({total:items.length,subtitles:items.filter(l=>l.subtitle!==null&&l.subtitle!=='').length,voiced:items.filter(l=>l.voice).length,dubs:items.filter(l=>l.voice&&l.dubPack).length});
 for(const l of lines){const key=l.group||'Ungrouped';if(!groups.has(key))groups.set(key,[]);groups.get(key).push(l);}
 return {total:count(lines),groups:[...groups].map(([name,items])=>({name,...count(items)}))};
}
export function subtitleHint(text,duration){
 const characters=Array.from(text).length;
 const cps=duration>0?characters/duration:null;
 return {characters,cps,warn:cps!==null&&cps>20,valid:!!text.trim()&&characters<=2000};
}
export const draftKey=(pack,lang,id)=>`${pack}/${lang}/${id}`;
export function languageName(code){return ({th:'ไทย',en:'English',ja:'日本語'}[code]||code);}
