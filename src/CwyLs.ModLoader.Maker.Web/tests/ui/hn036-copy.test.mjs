import test from 'node:test';
import assert from 'node:assert/strict';
import en from '../../ui/i18n/en.js';
import th from '../../ui/i18n/th.js';
import {makeT} from '../../ui/i18n/i18n.js';
import {suggestPackName} from '../../ui/logic/pack-naming.js';
import {renderExportPage} from '../../ui/zones/packs-modal.js';
const expected={
  "langnote": "ตอนนี้เกมเป็นภาษา{game} แต่แพ็กนี้เป็นภาษา{pack} เกมจะยังไม่แสดงซับจากแพ็กนี้จนกว่าจะเปลี่ยนภาษาในเกมเป็นภาษา{pack}",
  "langnote_btn": "สลับไปแก้ภาษา{game}",
  "npLangWhy": "ภาษาที่มีในตัวเกมอยู่แล้ว (EN, JA, ZH, KO, PT-BR, RU) ใช้คอลัมน์ซับของตัวเกมโดยตรง จึงสร้างแพ็กภาษาใหม่สำหรับภาษาเหล่านี้ไม่ได้",
  "nextUnrecShort": "ถัดไปที่ยังไม่อัด",
  "bump_major": "เวอร์ชันหลัก",
  "bump_minor": "เวอร์ชันรอง",
  "usePresetIcon": "ใช้ไอคอนสำเร็จรูป",
  "chk_links": "ลิงก์เชิงสัญลักษณ์",
  "chk_subtitles": "ซับ",
  "vb_colSub": "ซับ",
  "search": "ค้นหาข้อความหรือ ID บรรทัด",
  "placeholder": "พิมพ์บทพูดของซาโตเนะตรงนี้… ไม่ต้องกดบันทึก",
  "ro_code": "ม็อดแบบโค้ดมี DLL ของตัวเอง จึงใส่ซับในนี้ไม่ได้",
  "revert": "ย้อนกลับมาใช้เวอร์ชันนี้",
  "o_gen": "เสียงที่ AI สร้าง",
  "tab_lines": "บทพูด",
  "take_in_use": "เทคนี้อยู่ในแพ็กแล้ว เอาออกจากแพ็กก่อนถึงจะลบได้",
  "e_take_in_use": "เทคนี้อยู่ในแพ็กแล้ว เอาออกจากแพ็กก่อนนะ",
  "t_in_pack_play_failed": "อยู่ในแพ็กแล้ว แต่เกมเล่นเสียงนี้ไม่สำเร็จ: {reason}",
  "e_export_blocked": "ยังส่งออกไม่ได้เพราะมีหัวข้อไม่ผ่าน แก้แล้วตรวจใหม่อีกครั้ง"
};
for(const [key,value] of Object.entries(expected)) test('HN029: '+key,()=>assert.equal(th[key],value));
test('HN011: shortcut copy distinguishes editor Enter from Ctrl/Cmd+Enter without changing help overlays',()=>{
  assert.match(en.k_done,/Enter: next in editor/);assert.match(en.k_done,/Ctrl\/Cmd\+Enter: done/);
  assert.match(th.k_done,/ถัดไปในช่องซับ/);assert.match(th.k_done,/Ctrl\/Cmd\+Enter/);
});
test('debt11: suggested names stay within 48 code points, retaining the language with long authors',()=>{
  for(const lang of ['en','th'])for(const author of ['A'.repeat(80),'😀'.repeat(80)])for(const langName of ['', 'ภาษา'.repeat(20)]){
    const name=suggestPackName({author,langName,T:makeT(lang)});assert.ok(Array.from(name).length<=48,name);assert.doesNotMatch(name,/[\uD800-\uDBFF]$/);
    if(!langName)assert.ok(name.includes(lang==='en'?'Thai':'ไทย'));
  }
});
const messages=[
 ['manifest','ok','manifest.json is valid.'],['manifest','error','manifest.json is missing.'],['manifest','error','manifest.json is not valid: invalid field'],['manifest','error',"manifest.json names pack 'other', not 'test'."],
 ['subtitles','ok','Every subtitle table parses.'],['subtitles','ok','No subtitle tables.'],['subtitles','ok','45 subtitle(s) in 2 table(s).'],['subtitles','error','2 subtitle table(s) have entries the game refuses.'],['subtitles','warning','2 subtitle table(s) are empty.'],
 ['audio','ok','Every voice file reads.'],['audio','ok','3 voice file(s).'],['audio','error','2 voice file(s) are empty, broken or in a format the game cannot play.'],['audio','warning','2 file(s) under audio/voice/ are ignored by the game or longer than 60 s.'],
 ['provenance','ok','No dubs, so no voice disclosure is needed.'],['provenance','ok','Every published dub has a matching disclosure.'],['provenance','ok','3 dub(s) disclosed.'],['provenance','warning','2 dub(s) have no voice disclosure (made outside Studio).'],['provenance','error','voice-provenance.json is not valid: malformed'],['provenance','error','2 published dub(s) are missing their audio file.'],['provenance','error','2 dub(s) changed after they were published; publish them again.'],
 ['size','ok','The pack fits in a browser download.'],['size','ok','1.0 MiB in 3 file(s).'],['size','warning','The pack is 300.0 MiB: too big to download in the browser; the zip is still saved in CWYLS Mods/.maker-exports.'],['size','error','2 file(s) are over 512 MiB.'],['size','error','The pack is 2000.0 MiB; an export may be at most 1984 MiB.'],
 ['progress','ok',"No languages to compare with the game's lines."],['progress','ok',"The game's lines are not loaded yet, so progress was not compared."],['progress','ok','Every line has its subtitle and dub.'],['progress','warning','th: 2 voiced line(s) without a dub, 4 without a subtitle.'],['progress','warning','th: 2 of 7 voiced line(s) without a dub, 4 of 8 line(s) without a subtitle; vi: 1 of 2 voiced line(s) without a dub, 5 of 9 line(s) without a subtitle.'],
 ['links','warning','2 link(s) are skipped; copy the real files into the pack instead.'],['links','ok','No links inside the pack.'],['links','error','Symbolic links cannot be exported.'],['audio','error','A future backend detail.']
];
const state=check=>({packs:[{id:'test',name:'Test',editable:true,version:'1.0.0'}],packId:'test',exportPreview:{packId:'test',version:'1.0.0',checks:[{id:check[0],severity:check[1],message:check[2],count:2,paths:['<unsafe>.wav']}]},features:['export']});
test('debt13: export check details translate every backend variant and retain escaped paths',()=>{
  for(const check of messages){
    const html=renderExportPage(state(check),{T:makeT('th'),lang:'th'});
    const detail=html.match(/<div class="chk-main"><b>[^]*?<\/b> <span>([^]*?)<\/span>/)?.[1];
    assert.ok(/[ก-๙]/.test(detail),check[2]+': '+detail);assert.ok(!detail.includes(check[2]),check[2]);assert.ok(html.includes('&lt;unsafe&gt;.wav'));assert.ok(!html.includes('<unsafe>'));
  }
});
test('HN026-6: export renderer refuses preview and receipt ownership mismatches',()=>{
  const s=state(messages[0]);s.exportResult={packId:'another',fileName:'another-1.0.0.zip'};s.exportPreview.packId='another';
  const html=renderExportPage(s,{T:makeT('en'),lang:'en'});assert.ok(!html.includes('another-1.0.0.zip'));assert.ok(!html.includes('manifest.json is valid.'));
});
