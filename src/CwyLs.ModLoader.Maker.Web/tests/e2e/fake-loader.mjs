// Starts W2's loopback fake loader v2 (tools/fake_loader.py, invented data, text work only) on a free port with a throwaway
// data folder, serving this web folder at /maker/ with the maker token injected into index.html. Not shipped.
// Safety: loopback only (the fake binds 127.0.0.1), port 0, bounded start-up, always killed (close, exit, signals).
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');

/** @returns {Promise<{url:string, base:string, token:string, data:string, stderr:()=>string, close:()=>Promise<void>}>} `url` ends with /maker/ */
export async function startFakeLoader({ token = 'b'.repeat(64), startupMs = 8000, tickSec = 0 } = {}) {
  const data = await mkdtemp(join(tmpdir(), 'cwyls-w3-fake-'));
  const child = spawn('python3', ['-u', join(ROOT, 'tools/fake_loader.py'), '--port', '0', '--web', ROOT, '--data', data, '--token', token, '--tick-sec', String(tickSec)], { stdio: ['ignore', 'pipe', 'pipe'] });
  let err = '', closed = false;
  child.stderr.on('data', (d) => { err += d; });
  const exited = new Promise((r) => child.once('exit', r));
  const close = async () => {
    if (closed) return; closed = true;
    try { child.kill('SIGINT'); } catch { /* gone */ }
    await Promise.race([exited, new Promise((r) => setTimeout(r, 2000))]);
    try { child.kill('SIGKILL'); } catch { /* gone */ }
    await rm(data, { recursive: true, force: true }).catch(() => {});
  };
  process.once('exit', () => { try { child.kill('SIGKILL'); } catch { /* gone */ } });
  for (const sig of ['SIGINT', 'SIGTERM']) process.once(sig, () => { try { child.kill('SIGKILL'); } catch { /* gone */ } process.exit(130); });
  const base = await new Promise((res, rej) => {
    let out = '';
    const timer = setTimeout(() => rej(new Error(`the fake loader did not start within ${startupMs} ms: ${err}`)), startupMs);
    child.once('error', (e) => { clearTimeout(timer); rej(e); });
    child.once('exit', () => { clearTimeout(timer); rej(new Error(`the fake loader exited while starting: ${err}`)); });
    child.stdout.on('data', (d) => { out += d; const m = out.match(/http:\/\/127\.0\.0\.1:\d+/); if (m) { clearTimeout(timer); res(m[0]); } });
  }).catch(async (e) => { await close(); throw e; });
  return { base, url: `${base}/maker/`, token, data, stderr: () => err, close };
}
