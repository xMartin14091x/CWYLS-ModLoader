export const moments = [
 ['Focus timer', 'pomodoro.start', 'Focus begins', 'When a focus timer starts.'],
 ['Focus timer', 'pomodoro.end', 'A little break', 'When a focus timer ends.'],
 ['Focus timer', 'timer', 'Timer moments', 'When the regular timer changes.'],
 ['Game start', 'gamestart.any', 'Welcome back', 'On any game launch.'],
 ['Game start', 'gamestart.newday', 'A new day', 'The first visit of a new day.'],
 ['Game start', 'gamestart.sameday', 'Back again', 'Another visit on the same day.'],
 ['Game start', 'gamestart.days3to14', 'Getting familiar', 'Visits on days 3 through 14.'],
 ['Game start', 'gamestart.days15to30', 'Settling in', 'Visits on days 15 through 30.'],
 ['Game start', 'gamestart.days31plus', 'Old friends', 'Visits after day 30.'],
 ['Game start', 'gamestart.login100', 'One hundred hellos', 'The 100th login.'],
 ['Clicks', 'click.normal', 'A little hello', 'When the player clicks Satone.'],
 ['Clicks', 'click.work', 'Working together', 'A click while working.'],
 ['Clicks', 'click.work.morning', 'Morning company', 'A click while working in the morning.'],
 ['Self-talk', 'selftalk.time.7', 'Seven in the morning', 'A quiet thought around 7 a.m.'],
 ['Self-talk', 'selftalk.worked.2h', 'Two hours in', 'After two hours of work.'],
 ['Everywhere', '*', 'Any little moment', 'A line that can join any moment.']
];
export const materials = ['mt_Outfit','mt_Body','mt_Face','mt_Hairpin','mt_Items','mt_Tee_A','mt_Tee_B','mt_desk_1','mt_desktop','mt_window'];
export const sampleStrings = [
 ['ui.focus.begin','A small beginning is still a beginning.'],
 ['ui.focus.rest','Make a little room for a pause.'],
 ['ui.desk.note','There is room for your next idea.'],
 ['ui.weather.rain','A rainy afternoon, a fresh page.'],
 ['ui.timer.ready','Your quiet time is ready.'],
 ['ui.goodnight','Leave a little for tomorrow.']
];
export function seed() { return {
 pack:{name:'Rainy Study Lines',id:'martin.rainylines',version:'0.1.0',author:'Martin',description:'A little company for rainy afternoons. Gentle encouragement, in English and Thai.',languages:['EN','TH']},
 lines:[
 {id:'rain-01',pool:'pomodoro.start',text:{EN:'The rain can keep us company. Let’s begin.',TH:'ให้เสียงฝนอยู่เป็นเพื่อนเรา มาเริ่มกันนะ'},weight:.5,expression:'soft smile',motion:'nod',takes:{}},
 {id:'rain-02',pool:'pomodoro.start',text:{EN:'One small task. Then another. We’ve got this.',TH:'ทีละงานเล็ก ๆ นะ เราทำได้อยู่แล้ว'},weight:.2,expression:'smile',motion:'wave',takes:{}},
 {id:'rain-03',pool:'pomodoro.start',text:{EN:'I’ll watch the clouds while you find your focus.',TH:''},weight:.1,expression:'calm',motion:'still',takes:{}},
 {id:'rain-04',pool:'pomodoro.end',text:{EN:'Your tea deserves a little attention now.',TH:'ได้เวลาพักมาจิบชาหน่อยแล้วนะ'},weight:.6,expression:'smile',motion:'nod',takes:{}},
 {id:'rain-05',pool:'pomodoro.end',text:{EN:'A whole page closer. Take a breath.',TH:'ใกล้ขึ้นอีกหน้าหนึ่งแล้ว พักหายใจหน่อยนะ'},weight:.25,expression:'soft smile',motion:'still',takes:{}},
 {id:'rain-06',pool:'gamestart.any',text:{EN:'You’re here. The afternoon feels warmer already.',TH:'มาแล้วเหรอ บ่ายนี้รู้สึกอบอุ่นขึ้นเลย'},weight:.5,expression:'smile',motion:'wave',takes:{}},
 {id:'rain-07',pool:'gamestart.any',text:{EN:'Shall we make a little space for a good day?',TH:'มาหามุมเล็ก ๆ ให้วันนี้เป็นวันที่ดีกันไหม'},weight:.3,expression:'soft smile',motion:'nod',takes:{}},
 {id:'rain-08',pool:'*',text:{EN:'No need to rush the good things.',TH:''},weight:.15,expression:'calm',motion:'still',takes:{}}
 ],textures:{},translations:{TH:{'ui.focus.begin':'เริ่มต้นเล็ก ๆ ก็ถือว่าได้เริ่มแล้วนะ','ui.focus.rest':'เว้นที่ให้การพักสักหน่อยนะ','ui.desk.note':'ยังมีที่ว่างให้ไอเดียต่อไปของคุณ'},EN:{}},
 scenes:[{id:'scene-rain',name:'A rainy afternoon',rows:[{type:'Expression',value:'soft smile'},{type:'Say',value:'Listen. The rain found its rhythm.'},{type:'Wait',value:'2'},{type:'Motion',value:'nod'},{type:'Say',value:'Let’s find ours, too.'}]}],rules:[false,false,false,false]
}; }
