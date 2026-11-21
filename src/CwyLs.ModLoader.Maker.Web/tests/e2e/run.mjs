// The screenshot matrix and checks of PLAN §4.2 (headless Chrome over CDP, bounded).
//   perl -e 'alarm 600; exec @ARGV' node tests/e2e/run.mjs --matrix [--only state] [--sizes 1440x900] [--langs th] [--shots dir]
// For every size x language x state: exact viewport, load the app on the dev backend, then check: no page scroll, no clipped
// Thai (with a negative test that must fail first), no zone overflow, open menus inside the viewport, no console errors or
// warnings, no failed loads, and no request outside this loopback server. Writes shots/checks.json and PNGs.
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, setViewport, loadStudio, screenshot } from './cdp.mjs';
import { serveWeb } from './server.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '../..');
const args = process.argv.slice(2);
const opt = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : fallback; };

const STATES = [['01-normal', 'normal'], ['02-recording-countdown', 'countdown'], ['03-recording-level', 'recording'], ['04-qa-warning', 'qa'],
  ['05-game-paused', 'paused'], ['06-mic-not-set-up', 'nomic'], ['07-read-only-pack', 'readonly'], ['08-first-run-empty-pack', 'firstrun'],
  ['09-game-not-connected', 'offline'], ['10-pack-picker', 'picker'], ["11-history-open", "history"], ["12-edit-conflict", "conflict"], ["13-game-in-background", "unfocused"], ["14-consent-statement", "consent"], ["15-no-dubs-in-loader", "nodubs"], ["16-mic-off", "micoff"], ["17-other-pack-wins", "otherpack"], ["18-game-said-a-line", "spoken"], ["19-language-mismatch", "langmismatch"], ["20-packs-view", "packs"], ["21-new-pack-form", "newpack"], ["22-export-page", "export"], ["23-settings-page", "settings"], ["24-voice-lines-page", "lines"]];
const sizes = (opt('sizes', '1180x700,1280x720,1440x900,1920x1080')).split(',').map((s) => s.split('x').map(Number));
const langs = opt('langs', 'th,en').split(',');
const only = opt('only', null);
const shots = opt('shots', null);

const watchdog = setTimeout(() => { console.error('WATCHDOG: the run took too long, giving up'); process.exit(2); }, Number(opt('timeout', 540)) * 1000);
watchdog.unref();

const server = await serveWeb(ROOT);
const chrome = await launchChrome();
const results = [];
let failed = 0;
try {
  const page = await openPage(chrome.port);
  const checkScript = await readFile(join(HERE, 'page-checks.js'), 'utf8');
  // the negative test first: if the clip check cannot fail, nothing below means anything
  await setViewport(page, 1440, 900);
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=th`);
  await page.eval(checkScript);
  const neg = await page.eval('window.__pmChecks.negative()');
  console.log('negative clip test:', JSON.stringify(neg));
  if (!neg.flagsTight || !neg.passesLoose) { console.error('FAIL: the Thai clip check does not flag a tight box / pass a loose one'); failed++; }

  for (const [w, h] of sizes) {
    await setViewport(page, w, h);
    for (const lang of langs) {
      for (const [label, state] of STATES) {
        if (only && only !== state) continue;
        const url = `${server.url}index.html?ui=2&backend=dev&fixture=${state}&lang=${lang}`;
        const row = { file: `${w}x${h}-${lang}-${label}`, size: `${w}x${h}`, lang, state };
        try {
          await loadStudio(page, url);
          await page.eval(checkScript);
          const c = await page.eval('window.__pmChecks.run()');
          const foreign = page.requests.filter((u) => !u.startsWith(server.url) && !u.startsWith('data:') && !u.startsWith('about:'));
          Object.assign(row, { noPageScroll: c.noPageScroll, scroll: [c.scrollW, c.scrollH], clippedThai: c.clipped, zonesOverflowing: c.overflowing, overlaps: c.overlaps, problems: page.problems.slice(), foreignRequests: foreign });
          row.ok = c.noPageScroll && !c.clipped.length && !c.overflowing.length && !c.overlaps.length && !page.problems.length && !foreign.length;
          if (shots) { await mkdir(shots, { recursive: true }); await screenshot(page, join(shots, `${row.file}.png`)); }
        } catch (e) { row.ok = false; row.error = String(e.message || e); }
        if (!row.ok) failed++;
        results.push(row);
        console.log(`${row.ok ? 'ok  ' : 'FAIL'} ${row.file}${row.ok ? '' : ` ${JSON.stringify({ e: row.error, s: row.noPageScroll === false ? row.scroll : undefined, c: row.clippedThai?.slice(0, 2), o: row.zonesOverflowing?.slice(0, 2), v: row.overlaps, p: row.problems?.slice(0, 3), f: row.foreignRequests })}`}`);
      }
    }
  }
  page.close();
} finally {
  await chrome.close();
  await server.close();
}
if (shots) await writeFile(join(shots, 'checks.json'), JSON.stringify({ negative: 'see log', results }, null, 1));
console.log(`\n${results.length - failed >= 0 ? results.filter((r) => r.ok).length : 0}/${results.length} runs ok${failed ? `, ${failed} problem(s)` : ''}`);
process.exit(failed ? 1 : 0);
