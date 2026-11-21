import test from 'node:test';
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
test('storage folder and remedy appear as plain text on the page in EN and TH', async () => {
  const server = await serveWeb(root);
  let chrome, page;
  try {
    chrome = await launchChrome({extraArgs:['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1']});
    page = await openPage(chrome.port);
    const message = 'Exports at /synthetic/.maker-exports/my.pack: move older exports elsewhere. <img src=x onerror=alert(1)>';
    for (const lang of ['en', 'th']) {
      await loadStudio(page, `${server.url}index.html?backend=dev&lang=${lang}`);
      await page.eval(`(async () => {
        const {StudioError} = await import('./ui/api.js');
        __studio.conn.api.createPack = async () => { throw new StudioError('storage_limit', ${JSON.stringify(message)}, {status:409}); };
        await __studio.actions.createPack({name:'Storage toast', lang:'th'});
      })()`);
      const result = await page.eval(`({text:document.querySelector('#studio-toasts').textContent, images:document.querySelectorAll('#studio-toasts img').length})`);
      assert.ok(result.text.includes(message), `${lang}: ${result.text}`);
      assert.equal(result.images, 0, 'backend text must not become markup');
      assert.deepEqual(page.problems, []);
      assert.deepEqual(page.requests.filter(u => !u.startsWith(server.url) && !u.startsWith('data:')), []);
    }
  } finally { page?.close(); await chrome?.close(); await server.close(); }
});
