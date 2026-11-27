'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const zlib = require('node:zlib');
const { handleRequest, toHttpResponse, refererFor } = require('../function/src/lib/handler');

// A small web site on this machine. HazeGate refuses addresses like 127.0.0.1, so these tests use the
// allowPrivateNetworks option, which only tests can set.
let server;
let base;
const seen = [];

test.before(async () => {
  server = http.createServer((request, response) => {
    seen.push({ url: request.url, headers: request.headers, method: request.method });
    const send = (status, type, body, headers = {}) => {
      response.writeHead(status, { 'content-type': type, ...headers });
      response.end(body);
    };

    switch (request.url) {
      case '/page':
        send(200, 'text/html; charset=utf-8', '<html><head><link rel="stylesheet" href="/style.css"><script src="/app.js"></script>' +
          '<script type="module" src="/main.js"></script></head><body>Hellö</body></html>', { 'set-cookie': ['a=1; Path=/', 'b=2'] });
        break;
      case '/style.css':
        send(200, 'text/css', '@import "/imported.css"; body { color: red }');
        break;
      case '/imported.css':
        send(200, 'text/css', 'p { margin: 0 }');
        break;
      case '/app.js':
        send(200, 'application/javascript', 'console.log(1)');
        break;
      case '/main.js':
        send(200, 'text/javascript', 'import "./dep.js"; import x from "bare";');
        break;
      case '/dep.js':
        send(302, 'text/plain', '', { location: '/dep-final.js' });
        break;
      case '/dep-final.js':
        send(200, 'text/javascript', 'export const x = 1;');
        break;
      case '/redirect':
        send(301, 'text/html', 'moved', { location: '/page' });
        break;
      case '/gzip':
        response.writeHead(200, { 'content-type': 'text/plain', 'content-encoding': 'gzip' });
        response.end(zlib.gzipSync('compressed text'));
        break;
      case '/binary':
        send(200, 'image/png', Buffer.from([0x89, 0x50, 0x4e, 0x47, 0xff, 0x00]));
        break;
      case '/with-redirect?article=1':
        send(200, 'text/html', '<script src="/cross-origin.js"></script>');
        break;
      case '/cross-origin.js':
        send(302, 'text/plain', '', { location: base.replace('127.0.0.1', 'localhost') + '/app.js' });
        break;
      case '/bundle':
        send(200, 'text/html', '<script src="/one.js"></script><script src="/two.js"></script>');
        break;
      case '/one.js':
      case '/two.js':
        send(200, 'text/javascript', Buffer.alloc(5 * 1024 * 1024, 32));
        break;
      case '/big':
        send(200, 'text/plain', 'x'.repeat(5000), { 'content-length': 5000 });
        break;
      case '/echo': {
        const chunks = [];
        request.on('data', (chunk) => chunks.push(chunk));
        request.on('end', () => send(200, 'application/json', JSON.stringify({
          method: request.method,
          body: Buffer.concat(chunks).toString('utf8'),
          cookie: request.headers.cookie || null,
          custom: request.headers['x-custom'] || null,
          forwarded: request.headers['x-forwarded-for'] || null,
          type: request.headers['content-type'] || null,
        })));
        break;
      }
      default:
        send(404, 'text/plain', 'not found');
    }
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  base = `http://127.0.0.1:${server.address().port}`;
});

test.after(() => new Promise((resolve) => server.close(resolve)));

const local = { allowPrivateNetworks: true };
const post = (json, options = local) => handleRequest({ method: 'POST', body: Buffer.from(JSON.stringify(json)) }, options);

test('GET answers with the service name (Settings -> Test)', async () => {
  const answer = await handleRequest({ method: 'GET', body: Buffer.alloc(0) });
  assert.equal(answer.status, 200);
  assert.equal(answer.json.service, 'HazeGate');
  assert.equal(answer.json.protocol, 1);
});

test('a page comes with its stylesheets, scripts and their imports', async () => {
  const answer = await post({ v: 1, mode: 'page', url: `${base}/page`, bundle: true });
  assert.equal(answer.status, 200);
  assert.equal(answer.json.status, 200);
  assert.match(answer.json.text, /Hellö/);
  assert.deepEqual(answer.json.setCookies, ['a=1; Path=/', 'b=2']);
  const byUrl = Object.fromEntries(answer.json.resources.map((resource) => [new URL(resource.url).pathname, resource]));
  assert.deepEqual(Object.keys(byUrl).sort(), ['/app.js', '/dep.js', '/imported.css', '/main.js', '/style.css']);
  assert.equal(byUrl['/style.css'].text, '@import "/imported.css"; body { color: red }');
  assert.equal(byUrl['/imported.css'].text, 'p { margin: 0 }');
  assert.equal(byUrl['/dep.js'].finalUrl, `${base}/dep-final.js`);
  assert.deepEqual(byUrl['/dep.js'].redirects, [`${base}/dep.js`]);
  assert.equal(byUrl['/dep.js'].text, 'export const x = 1;');
  // Parts of the page are downloaded without the page's cookies.
  assert.ok(seen.filter((request) => request.url === '/style.css').every((request) => !request.headers.cookie));
});

test('redirects are returned, not followed', async () => {
  const answer = await post({ v: 1, mode: 'page', url: `${base}/redirect`, bundle: true });
  assert.equal(answer.json.status, 301);
  assert.equal(answer.json.location, `${base}/page`);
  assert.deepEqual(answer.json.resources, []);
});

test('compressed answers are decompressed and binary bodies are Base64', async () => {
  const text = await post({ v: 1, mode: 'resource', url: `${base}/gzip` });
  assert.equal(text.json.text, 'compressed text');
  const binary = await post({ v: 1, mode: 'resource', url: `${base}/binary` });
  assert.equal(binary.json.text, undefined);
  assert.deepEqual(Buffer.from(binary.json.body, 'base64'), Buffer.from([0x89, 0x50, 0x4e, 0x47, 0xff, 0x00]));
});

test('bodies, cookies and allowed headers are passed on; forbidden headers are not', async () => {
  const answer = await post({
    v: 1,
    mode: 'resource',
    url: `${base}/echo`,
    method: 'POST',
    body: Buffer.from('a=1&b=2').toString('base64'),
    contentType: 'application/x-www-form-urlencoded',
    cookie: 'session=abc',
    headers: { 'X-Custom': 'yes', 'X-Forwarded-For': '1.2.3.4', 'Bad Name': 'x' },
  });
  const echoed = JSON.parse(answer.json.text);
  assert.deepEqual(echoed, {
    method: 'POST',
    body: 'a=1&b=2',
    cookie: 'session=abc',
    custom: 'yes',
    forwarded: null,
    type: 'application/x-www-form-urlencoded',
  });
});

test('answers larger than maxBytes are refused', async () => {
  const answer = await post({ v: 1, mode: 'resource', url: `${base}/big`, maxBytes: 1000 });
  assert.equal(answer.status, 502);
  assert.match(answer.json.error, /larger/);
});

test('private network addresses are refused without the test option', async () => {
  for (const url of [`${base}/page`, 'http://localhost/', 'http://169.254.169.254/metadata', 'http://[::1]/', 'http://2130706433/', 'http://10.0.0.1/']) {
    const answer = await post({ v: 1, mode: 'page', url }, {});
    assert.equal(answer.status, 403, url);
    assert.match(answer.json.error, /private networks/, url);
  }
});

test('bad requests are refused', async () => {
  assert.equal((await handleRequest({ method: 'POST', body: Buffer.from('not json') })).status, 400);
  assert.equal((await post({ v: 2, mode: 'page', url: 'https://example.com/' })).status, 400);
  assert.equal((await post({ v: 1, mode: 'other', url: 'https://example.com/' })).status, 400);
  assert.equal((await post({ v: 1, mode: 'page', url: 'file:///etc/passwd' })).status, 403);
  assert.equal((await post({ v: 1, mode: 'page', url: 'https://example.com:25/' })).status, 403);
  assert.equal((await post({ v: 1, mode: 'page', url: 'https://example.com/', method: 'DELETE' })).status, 400);
  assert.equal((await post({ v: 1, mode: 'resource', url: 'https://example.com/', body: '%%%' })).status, 400);
  assert.equal((await handleRequest({ method: 'PUT', body: Buffer.alloc(0) })).status, 405);
});

test('the answer is compressed with gzip when the caller accepts it', () => {
  const answer = { status: 200, json: { text: 'x'.repeat(5000) } };
  const compressed = toHttpResponse(answer, 'gzip, deflate, br');
  assert.equal(compressed.headers['content-encoding'], 'gzip');
  assert.deepEqual(JSON.parse(zlib.gunzipSync(compressed.body).toString('utf8')), answer.json);
  const plain = toHttpResponse(answer, null);
  assert.equal(plain.headers['content-encoding'], undefined);
  assert.deepEqual(JSON.parse(plain.body.toString('utf8')), answer.json);
});

test('the Referer follows the browsers\' default policy', () => {
  assert.equal(refererFor('https://a.example/page?q=1#x', new URL('https://a.example/other')), 'https://a.example/page?q=1');
  assert.equal(refererFor('https://a.example/page?q=1', new URL('https://b.example/')), 'https://a.example/');
  assert.equal(refererFor('https://a.example/page', new URL('http://a.example/')), null);
  assert.equal(refererFor('', new URL('https://a.example/')), null);
});

test('HEAD succeeds when the advertised representation exceeds the body limit', async () => {
  const answer = await post({ mode: 'resource', method: 'HEAD', url: base + '/big', maxBytes: 100 });
  assert.equal(answer.json.status, 200);
  assert.equal(answer.json.text, '');
});

test('parallel resource downloads share the bundle byte limit', async () => {
  const answer = await post({ mode: 'page', url: base + '/bundle', bundle: true });
  const resources = answer.json.resources;
  assert.equal(resources.filter(resource => resource.text?.length === 5 * 1024 * 1024).length, 1);
  assert.equal(resources.filter(resource => resource.error).length, 1);
});

test('bundled resources send only the referring origin across a redirect to another host', async () => {
  const answer = await post({ mode: 'page', url: base + '/with-redirect?article=1', bundle: true });
  assert.equal(answer.json.resources[0].status, 200);
  assert.equal(seen.findLast(request => request.url === '/app.js').headers.referer, base + '/');
});
