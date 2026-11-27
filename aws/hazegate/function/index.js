'use strict';

const { createHash, timingSafeEqual } = require('node:crypto');
const { handleRequest, toHttpResponse } = require('./src/lib/handler');

// Synchronous Lambda invocations allow 6 MiB including the serialized proxy envelope.
const MAX_RESPONSE_BYTES = 6 * 1024 * 1024 - 1024;
const digest = value => createHash('sha256').update(value).digest();

function proxyResponse(answer, encoding) {
  const { status, headers, body } = toHttpResponse(answer, encoding);
  const binary = headers['content-encoding'] === 'gzip';
  return { statusCode: status, headers, isBase64Encoded: binary, body: body.toString(binary ? 'base64' : 'utf8') };
}

function boundedResponse(answer, encoding) {
  let response = proxyResponse(answer, encoding);
  if (Buffer.byteLength(JSON.stringify(response)) <= MAX_RESPONSE_BYTES) return response;

  // PhantomHaze loads missing bundle entries individually through its resource transport.
  if (answer.json.resources?.length) {
    response = proxyResponse({ ...answer, json: { ...answer.json, resources: [] } }, encoding);
    if (Buffer.byteLength(JSON.stringify(response)) <= MAX_RESPONSE_BYTES) return response;
  }
  return proxyResponse({ status: 413, json: { v: 1, error: 'This page or resource exceeds the AWS response size limit.' } });
}

async function handler(event) {
  const token = process.env.HAZEGATE_TOKEN;
  const headers = event.headers || {};
  // API Gateway HTTP APIs lowercase header names. Only the protocol body goes to the downloader.
  if (!token || !timingSafeEqual(digest(headers.authorization || ''), digest('Bearer ' + token))) {
    return proxyResponse({ status: 401, json: { v: 1, error: 'Check the HazeGate access token.' } });
  }

  const method = event.requestContext.http.method;
  const body = Buffer.from(event.body || '', event.isBase64Encoded ? 'base64' : 'utf8');
  try {
    const answer = await handleRequest({ method, body });
    return boundedResponse(answer, headers['accept-encoding']);
  } catch {
    console.error('HazeGate request failed', event.requestContext.requestId);
    return proxyResponse({ status: 500, json: { v: 1, error: 'HazeGate could not process this request.' } });
  }
}

module.exports = { handler, boundedResponse };
