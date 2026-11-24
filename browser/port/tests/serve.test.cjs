// browser/serve.py, the local page server: what it serves and what it forwards to the game server.
const assert = require('node:assert/strict');
const test = require('node:test');
const http = require('node:http');
const net = require('node:net');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const ROOT = path.resolve(__dirname, '../../..');
const PYTHON = ['python', 'python3', 'py'].find(cmd => {
  try { return spawnSync(cmd, ['--version']).status === 0; } catch { return false; }
});
const freePort = () => new Promise(resolve => {
  const s = net.createServer().listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
});
const request = (port, method, rawPath, headers = {}, body = '') => new Promise((resolve, reject) => {
  const req = http.request({ host: '127.0.0.1', port, method, path: rawPath, headers }, res => {
    const chunks = [];
    res.on('data', c => chunks.push(c));
    res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString('utf8') }));
  });
  req.on('error', reject);
  req.end(body);
});

test('serve.py serves allow-listed pages only, answers HEAD like GET, and forwards the game headers', { skip: !PYTHON && 'python not found' }, async t => {
  const seen = [];
  const backend = http.createServer((req, res) => {
    seen.push({ url: req.url, headers: req.headers });
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('gateway-reply');
  });
  await new Promise(resolve => backend.listen(0, '127.0.0.1', resolve));
  const webPort = await freePort();
  const server = spawn(PYTHON, [path.join(ROOT, 'browser', 'serve.py'), String(webPort), String(backend.address().port)], { cwd: ROOT, stdio: 'ignore' });
  t.after(() => { server.kill(); backend.close(); });
  for (let i = 0; i < 100; i++) {
    try { await request(webPort, 'HEAD', '/kns.html'); break; } catch { await new Promise(r => setTimeout(r, 100)); }
  }

  for (const blocked of ['/assets/anything.json', '/lobby.html', '/lobby', '/serve.py', '/port/web.js']) {
    assert.equal((await request(webPort, 'GET', blocked)).status, 404, 'GET ' + blocked);
    assert.equal((await request(webPort, 'HEAD', blocked)).status, 404, 'HEAD ' + blocked);
  }
  const page = await request(webPort, 'GET', '/kns.html');
  assert.equal(page.status, 200);
  const head = await request(webPort, 'HEAD', '/kns.html');
  assert.equal(head.status, 200);
  assert.equal(head.headers['content-length'], page.headers['content-length']);
  assert.equal(head.body, '');
  assert.equal((await request(webPort, 'HEAD', '/studio')).status, 200);

  const game = { USERNO: '42', SESSIONKEY: 'sk_42', CLIENTVERSION: '1.4.0', CLIENTBUNDLEIDENTIFIER: 'com.example',
    CLIENTLANGUAGE: 'en', TIMESTAMP: '1794000000', TIMESIGNATURE: 'sig' };
  const reply = await request(webPort, 'POST', '/1mhero/gateway.php',
    { ...game, 'Content-Type': 'text/plain', Origin: 'http://elsewhere', 'Accept-Encoding': 'gzip' }, 'encrypted-body');
  assert.equal(reply.status, 200);
  assert.equal(reply.body, 'gateway-reply');
  const forwarded = seen.at(-1);
  assert.equal(forwarded.url, '/1mhero/gateway.php');
  for (const [name, value] of Object.entries(game)) assert.equal(forwarded.headers[name.toLowerCase()], value, name);
  assert.equal(forwarded.headers['content-type'], 'text/plain');
  assert.equal(forwarded.headers.origin, undefined);
  assert.notEqual(forwarded.headers['accept-encoding'], 'gzip');
});
