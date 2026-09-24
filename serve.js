// Minimal static file server. No dependencies.
// Usage: node serve.js [root] [port]
//   node serve.js                                  -> this folder, port 8130
//   node serve.js src/BlazorRunner/dist/wwwroot 8140
const http = require('http');
const fs = require('fs');
const path = require('path');

const root = path.resolve(process.argv[2] || __dirname);
const port = Number(process.argv[3] || 8130);

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.wasm': 'application/wasm',
  '.dat': 'application/octet-stream',
  '.blat': 'application/octet-stream',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.png': 'image/png',
};

http
  .createServer((req, res) => {
    const urlPath = decodeURIComponent((req.url || '/').split('?')[0]);
    const filePath = path.join(root, urlPath === '/' ? 'index.html' : urlPath);
    if (!filePath.startsWith(root)) {
      res.writeHead(403).end('forbidden');
      return;
    }
    fs.readFile(filePath, (err, data) => {
      if (err) {
        // Single-page-app fallback: a routed path such as /counter has no file of its own, so hand
        // back the app and let Blazor's Router deal with it.
        if (path.extname(filePath) === '') {
          fs.readFile(path.join(root, 'index.html'), (indexErr, indexData) => {
            if (indexErr) {
              res.writeHead(404).end('not found: ' + urlPath);
              return;
            }
            res.writeHead(200, {
              'Content-Type': 'text/html; charset=utf-8',
              'Cache-Control': 'no-store',
            });
            res.end(indexData);
          });
          return;
        }
        res.writeHead(404).end('not found: ' + urlPath);
        return;
      }
      res.writeHead(200, {
        'Content-Type': types[path.extname(filePath)] || 'application/octet-stream',
        'Cache-Control': 'no-store',
      });
      res.end(data);
    });
  })
  .listen(port, () => console.log('serving ' + root + ' on http://localhost:' + port + '/'));
