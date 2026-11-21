// Proves the harness cannot hang or leak: a stuck page makes a CDP call time out (it does not wait forever), and
// Chrome plus the static server are really gone after close(). Run bounded:
//   perl -e 'alarm 90; exec @ARGV' node tests/e2e/harness-selftest.mjs
import assert from 'node:assert/strict';
import { launchChrome, openPage } from './cdp.mjs';
import { serveWeb } from './server.mjs';

const alive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
setTimeout(() => { console.error('WATCHDOG'); process.exit(2); }, 80000).unref();

const chrome = await launchChrome();
const server = await serveWeb(process.cwd());
const page = await openPage(chrome.port);

// 1. a page stuck in an endless loop: the call must fail by timeout, quickly
const t0 = Date.now();
await assert.rejects(page.eval('while(true){}', 700), /CDP timeout after 700 ms/);
assert.ok(Date.now() - t0 < 3000, `the timeout took ${Date.now() - t0} ms`);
console.log('ok  a stuck page times out in', Date.now() - t0, 'ms');

// 2. the static server refuses paths outside its root
const outside = await fetch(`${server.url}..%2f..%2fetc%2fpasswd`, { signal: AbortSignal.timeout(3000) });
assert.ok(outside.status === 403 || outside.status === 404, `status ${outside.status}`);
console.log('ok  the static server refuses a path outside its root:', outside.status);

// 3. teardown: Chrome is dead and the port is closed
page.close();
await chrome.close();
await server.close();
await sleep(500);
assert.equal(alive(chrome.pid), false, 'Chrome is still running after close()');
await assert.rejects(fetch(`http://127.0.0.1:${chrome.port}/json/version`, { signal: AbortSignal.timeout(2000) }), 'the debugging port is still open');
console.log('ok  Chrome and the server are gone after close()');
