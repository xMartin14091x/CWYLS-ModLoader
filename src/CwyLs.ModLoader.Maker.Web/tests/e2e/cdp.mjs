// Zero-dependency headless Chrome driver over the DevTools protocol (Node 22 global WebSocket). Not shipped.
// Safety: a free loopback port, a throwaway profile, every CDP message has a timeout, and Chrome is always killed
// (finally, process exit, signals). Never use --screenshot with --virtual-time-budget on a page that long-polls.
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const CHROME = process.env.CHROME_BIN || '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function freePort() {
  return new Promise((resolve, reject) => {
    const srv = createServer();
    srv.once('error', reject);
    srv.listen(0, '127.0.0.1', () => { const { port } = srv.address(); srv.close(() => resolve(port)); });
  });
}

/** Starts headless Chrome. Returns {port, close()}. */
export async function launchChrome({ extraArgs = [] } = {}) {
  const port = await freePort();
  const dir = await mkdtemp(join(tmpdir(), 'cwyls-w3-chrome-'));
  // Exports a test triggers must land in the throwaway profile, never in the maintainer's ~/Downloads (201 test zips
  // piled up there by 2026-11-17); the folder goes away with the profile on close().
  await mkdir(join(dir, 'Default'), { recursive: true });
  await writeFile(join(dir, 'Default', 'Preferences'), JSON.stringify({ download: { default_directory: join(dir, 'downloads'), prompt_for_download: false } }));
  const child = spawn(CHROME, [
    '--headless=new', `--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1', `--user-data-dir=${dir}`,
    // Inside a sandboxed shell Chrome's own seatbelt cannot start ("Failed to initialize sandbox"); set CWYLS_CHROME_NO_SANDBOX=1
    // there. It only ever loads this loopback server. The crash reporter would write outside the throwaway profile.
    ...(process.env.CWYLS_CHROME_NO_SANDBOX === '1' ? ['--no-sandbox'] : []), '--disable-gpu', '--disable-crash-reporter',
    '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-sync', '--disable-background-networking',
    '--autoplay-policy=no-user-gesture-required', '--force-device-scale-factor=1', '--window-size=1920,1080', ...extraArgs, 'about:blank'
  ], { stdio: 'ignore' });
  let closed = false;
  const close = async () => {
    if (closed) return; closed = true;
    try { child.kill('SIGKILL'); } catch { /* already gone */ }
    await rm(dir, { recursive: true, force: true }).catch(() => {});
  };
  const onExit = () => { try { child.kill('SIGKILL'); } catch { /* gone */ } };
  process.once('exit', onExit);
  for (const sig of ['SIGINT', 'SIGTERM']) process.once(sig, () => { onExit(); process.exit(130); });
  // wait for the debugging endpoint (bounded)
  const t0 = Date.now();
  for (;;) {
    try { const r = await fetch(`http://127.0.0.1:${port}/json/version`, { signal: AbortSignal.timeout(1000) }); if (r.ok) break; } catch { /* not up yet */ }
    if (Date.now() - t0 > 60000) { await close(); throw new Error('Chrome did not open its debugging port within 60 s'); } // 15 s was too short on a loaded build machine (RC5, 11-17)
    await sleep(150);
  }
  return { port, pid: child.pid, close };
}

export class Page {
  constructor(ws) {
    this.ws = ws; this.id = 0; this.pending = new Map(); this.handlers = new Map();
    ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject, timer } = this.pending.get(msg.id);
        clearTimeout(timer); this.pending.delete(msg.id);
        if (msg.error) reject(new Error(`${msg.error.message} (${msg.error.code})`)); else resolve(msg.result);
      } else if (msg.method) for (const h of this.handlers.get(msg.method) || []) h(msg.params);
    });
  }
  send(method, params = {}, timeoutMs = 30000) {
    const id = ++this.id;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`CDP timeout after ${timeoutMs} ms: ${method}`)); }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }
  on(method, fn) { if (!this.handlers.has(method)) this.handlers.set(method, []); this.handlers.get(method).push(fn); }
  /** Evaluates an expression in the page and returns its value (JSON-serialisable). */
  async eval(expression, timeoutMs = 30000) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, timeoutMs);
    if (r.exceptionDetails) throw new Error(`page exception: ${r.exceptionDetails.exception?.description || r.exceptionDetails.text}`);
    return r.result.value;
  }
  close() { try { this.ws.close(); } catch { /* closed */ } }
}

/** Opens a fresh tab and attaches the collectors the checks need. */
export async function openPage(port) {
  const tab = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT', signal: AbortSignal.timeout(5000) })).json();
  const ws = new WebSocket(tab.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', () => reject(new Error('websocket error')), { once: true }); setTimeout(() => reject(new Error('websocket open timeout')), 5000); });
  const page = new Page(ws);
  page.tabId = tab.id;
  page.problems = [];   // console errors/warnings, exceptions, failed loads
  page.requests = [];   // every URL the page asked for
  page.on('Runtime.consoleAPICalled', (p) => { if (p.type === 'error' || p.type === 'warning') page.problems.push(`console.${p.type}: ${p.args.map((a) => a.value ?? a.description ?? '').join(' ')}`); });
  page.on('Runtime.exceptionThrown', (p) => page.problems.push(`exception: ${p.exceptionDetails.exception?.description || p.exceptionDetails.text}`));
  page.on('Log.entryAdded', (p) => { if (p.entry.level === 'error' || p.entry.level === 'warning') page.problems.push(`log.${p.entry.level}: ${p.entry.text} ${p.entry.url || ''}`); });
  page.on('Network.loadingFailed', (p) => { if (!p.canceled) page.problems.push(`load failed: ${p.errorText} (${p.requestId})`); });
  page.on('Network.requestWillBeSent', (p) => page.requests.push(p.request.url));
  page.on('Network.responseReceived', (p) => { if (p.response.status >= 400) page.problems.push(`HTTP ${p.response.status}: ${p.response.url}`); });
  await Promise.all(['Runtime', 'Log', 'Network', 'Page'].map((d) => page.send(`${d}.enable`)));
  // Activate the headless test tab before frame-driven checks; a newly created target can be unfocused.
  await page.send('Page.bringToFront');
  return page;
}

export async function setViewport(page, width, height) {
  await page.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
}

/** Navigates and waits for `document.body.dataset.studio === 'ready'` (bounded). 60 s: on a loaded build machine (load
 *  average 40+ while other builds run) a cold start took over 20 s; this only bounds start-up, never an assertion. */
export async function loadStudio(page, url, { timeoutMs = 60000 } = {}) {
  page.problems.length = 0; page.requests.length = 0;
  await page.send('Page.navigate', { url });
  const t0 = Date.now();
  for (;;) {
    const state = await page.eval("document.body ? document.body.dataset.studio || '' : ''").catch(() => '');
    if (state === 'ready') return;
    if (Date.now() - t0 > timeoutMs) throw new Error(`the page did not become ready within ${timeoutMs} ms: ${url}`);
    await sleep(100);
  }
}

export async function screenshot(page, path) {
  const { writeFile, mkdir } = await import('node:fs/promises');
  const { dirname } = await import('node:path');
  await mkdir(dirname(path), { recursive: true });
  let res;
  try {
    res = await page.send('Page.captureScreenshot', { format: 'png' }, 15000);
  } catch {
    res = await page.send('Page.captureScreenshot', { format: 'png' }, 20000);
  }
  await writeFile(path, Buffer.from(res.data, 'base64'));
}
