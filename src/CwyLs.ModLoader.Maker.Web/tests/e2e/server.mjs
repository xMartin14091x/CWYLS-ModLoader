// A tiny loopback static server for the web app (no Python needed). Optionally injects a maker token meta tag.
// Not shipped. Serves only files under `root`, only on 127.0.0.1, with the content types the loader allows.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize, sep } from 'node:path';

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.ttf': 'font/ttf', '.woff2': 'font/woff2', '.txt': 'text/plain; charset=utf-8' };

/** @returns {Promise<{port:number, url:string, close():Promise<void>, hits:string[]}>} */
export function serveWeb(root, { token = null } = {}) {
  const hits = [];
  const server = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://127.0.0.1');
      let rel = decodeURIComponent(url.pathname).replace(/^\/+/, '') || 'index.html';
      const file = normalize(join(root, rel));
      if (!file.startsWith(root + sep)) { res.writeHead(403).end('no'); return; }
      hits.push(rel);
      let body = await readFile(file);
      if (rel === 'index.html' && token) body = Buffer.from(body.toString('utf8').replace('</head>', `  <meta name="cwyls-maker-token" content="${token}">\n</head>`));
      res.writeHead(200, { 'Content-Type': TYPES[extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' }).end(body);
    } catch { res.writeHead(404).end('not found'); }
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => {
    const { port } = server.address();
    resolve({ port, url: `http://127.0.0.1:${port}/`, hits, close: () => new Promise((r) => { server.closeAllConnections?.(); server.close(() => r()); }) });
  }));
}
