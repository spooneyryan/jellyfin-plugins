// Serves a folder over HTTP on 127.0.0.1 only, for testing a plugin catalog before it is published: the test catalog
// Package-Release.ps1 -BaseUrl writes (manifest.json, the zips and images/) is what a sandbox's plugin repository reads.
// GET and HEAD only, files inside --root only, no directory listings. Each request is logged as one line on stdout.
//
//   node scripts/serve-release.mjs --root artifacts/release-test --port 8115
import { createServer } from 'node:http';
import { createReadStream, realpathSync, statSync } from 'node:fs';
import { extname, join, resolve, sep } from 'node:path';

const args = process.argv.slice(2);
const option = (name) => {
  const at = args.indexOf(`--${name}`);
  return at >= 0 ? args[at + 1] : undefined;
};

const root = realpathSync(resolve(option('root') ?? 'artifacts/release-test'));
const port = Number(option('port'));
if (!Number.isInteger(port) || port < 1024 || port > 65535) {
  console.error('Pass --port <1024-65535>.');
  process.exit(2);
}

const types = { '.json': 'application/json', '.zip': 'application/zip', '.png': 'image/png', '.md': 'text/plain; charset=utf-8' };

const server = createServer((request, response) => {
  const finish = (status, headers = {}) => {
    response.writeHead(status, headers);
    console.log(`${request.method} ${request.url} ${status}`);
  };
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    finish(405, { Allow: 'GET, HEAD' });
    response.end();
    return;
  }

  let file;
  try {
    const path = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname);
    file = realpathSync(join(root, path));
    if (!file.startsWith(root + sep) || !statSync(file).isFile()) throw new Error('not a file');
  } catch {
    finish(404);
    response.end();
    return;
  }

  finish(200, { 'Content-Type': types[extname(file).toLowerCase()] ?? 'application/octet-stream', 'Content-Length': statSync(file).size, 'Cache-Control': 'no-store' });
  if (request.method === 'HEAD') response.end();
  else createReadStream(file).pipe(response);
});

server.listen(port, '127.0.0.1', () => console.log(`Serving ${root} on http://127.0.0.1:${port}/`));
