'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { gunzipSync } = require('node:zlib');
const { handler, boundedResponse } = require('../function');
const { buildHeaders } = require('../function/src/lib/handler');

const token = 'local-test-token';
test.before(() => { process.env.HAZEGATE_TOKEN = token; });
test.after(() => { delete process.env.HAZEGATE_TOKEN; });

function event(method, authorization = 'Bearer ' + token) {
  return { requestContext: { http: { method } }, headers: { authorization } };
}

test('checks the bearer token before handling either route', async () => {
  for (const method of ['GET', 'POST']) {
    for (const invalid of ['', 'Bearer wrong', token]) {
      assert.equal((await handler(event(method, invalid))).statusCode, 401);
    }
  }
  const response = await handler(event('GET'));
  assert.equal(response.statusCode, 200);
  assert.equal(JSON.parse(response.body).protocol, 1);
});

test('decodes HTTP API v2 plain and base64 bodies with the same network restrictions', async () => {
  const json = JSON.stringify({ v: 1, mode: 'resource', url: 'http://127.0.0.1/' });
  for (const isBase64Encoded of [true, false]) {
    const body = isBase64Encoded ? Buffer.from(json).toString('base64') : json;
    const response = await handler({ ...event('POST'), body, isBase64Encoded });
    assert.equal(response.statusCode, 403);
    assert.match(JSON.parse(response.body).error, /private networks/);
  }
});

test('gateway and AWS credentials cannot be forwarded by page headers', () => {
  const headers = buildHeaders({ headers: { Authorization: 'secret', 'X-Api-Key': 'secret',
    'X-Amz-Security-Token': 'secret', 'X-Example': 'allowed' } }, new URL('https://example.com/'), {});
  assert.equal(headers['x-example'], 'allowed');
  for (const key of ['authorization', 'x-api-key', 'x-amz-security-token']) assert.equal(headers[key], undefined);
});

test('gzip is encoded for API Gateway to send as binary', () => {
  const json = { text: 'A'.repeat(2000) };
  const response = boundedResponse({ status: 200, json }, 'gzip');
  assert.equal(response.isBase64Encoded, true);
  assert.equal(response.headers['content-encoding'], 'gzip');
  assert.deepEqual(JSON.parse(gunzipSync(Buffer.from(response.body, 'base64'))), json);
});

test('oversized envelopes drop optional resources or return a protocol error', () => {
  const large = '"'.repeat(4 * 1024 * 1024);
  const response = boundedResponse({ status: 200, json: { text: 'page', resources: [{ text: large }] } });
  assert.deepEqual(JSON.parse(response.body), { text: 'page', resources: [] });
  assert.equal(boundedResponse({ status: 200, json: { text: large } }).statusCode, 413);
});
