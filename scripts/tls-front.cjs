'use strict';
// Local-only TLS compatibility front end. Login domains are never redirected here.
const fs = require('node:fs');
const net = require('node:net');
const http = require('node:http');
const https = require('node:https');
const path = require('node:path');
const [certPath, keyPath, logPath, parentId] = process.argv.slice(2);
if (!certPath || !keyPath || !logPath || !parentId) throw new Error('Missing TLS frontend arguments');
function log(message) {
  const line = new Date().toISOString() + ' ' + message + '\n';
  if (fs.existsSync(logPath) && fs.statSync(logPath).size > 65536) fs.writeFileSync(logPath, '');
  fs.appendFileSync(logPath, line);
}
function route(req, res) {
  const route = req.url.split('?')[0];
  if (!['/growtopia/server_data.php', '/health'].includes(route) || !['GET', 'POST'].includes(req.method)) {
    res.writeHead(404); res.end(); return;
  }
  log('HTTP request: ' + req.method + ' ' + route);
  const headers = {...req.headers, host: '127.0.0.1', connection: 'close'};
  const request = http.request({host: '127.0.0.1', port: 80, method: req.method, path: route, headers}, response => {
    res.writeHead(response.statusCode, response.headers); response.pipe(res);
  });
  request.setTimeout(5000, () => request.destroy());
  request.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  req.pipe(request);
}
const plain = http.createServer(route);
const secure = https.createServer({cert: fs.readFileSync(certPath), key: fs.readFileSync(keyPath), minVersion: 'TLSv1.2'}, route);
secure.on('tlsClientError', error => log('TLS failure: ' + (error.code || 'unknown') + ' ' + error.message.split('\n')[0].slice(0, 200)));
plain.listen(9080, '127.0.0.1');
secure.listen(9443, '127.0.0.1');
const front = net.createServer(socket => {
  socket.setTimeout(10000, () => socket.destroy());
  socket.once('data', first => {
    const isTls = first[0] === 22;
    log('Incoming transport: ' + (isTls ? 'TLS' : 'HTTP') + '; prefix=' + first.subarray(0, 5).toString('hex'));
    socket.pause();
    const backend = net.createConnection({host: '127.0.0.1', port: isTls ? 9443 : 9080}, () => {
      backend.write(first); socket.pipe(backend); backend.pipe(socket); socket.resume();
    });
    backend.on('error', () => socket.destroy());
    socket.on('close', () => backend.destroy());
  });
  socket.on('error', () => {});
});
front.listen(443, '127.0.0.2', () => log('TLS frontend ready on 127.0.0.2:443'));
const web = http.createServer(route).listen(80, '127.0.0.2');
let misses = 0;
const timer = setInterval(() => {
  try { process.kill(Number(parentId), 0); } catch { shutdown(); return; }
  const request = http.get('http://127.0.0.1/health', response => { response.resume(); misses = response.statusCode === 200 ? 0 : misses + 1; });
  request.setTimeout(1500, () => request.destroy());
  request.on('error', () => { if (++misses >= 3) shutdown(); });
}, 2000);
function shutdown() { clearInterval(timer); front.close(); web.close(); secure.close(); plain.close(); setTimeout(() => process.exit(0), 250).unref(); }
process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
