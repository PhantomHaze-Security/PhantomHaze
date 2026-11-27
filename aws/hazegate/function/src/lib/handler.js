'use strict';

// The HazeGate protocol (version 1), independent of API Gateway so it can be tested: handle() takes the method, the headers
// and the body of a request, and returns the status, headers and body of the answer. README.md describes the protocol.

const zlib = require('node:zlib');
const { isUtf8 } = require('node:buffer');
const { fetchOnce, fetchFollowing, checkUrl, FetchError } = require('./fetcher');
const { findPageResources, findCssImports, findModuleImports } = require('./discover');
const { version } = require('../../package.json');

const PROTOCOL_VERSION = 1;
const MAX_REQUEST_BYTES = 12 * 1024 * 1024; // the JSON request, with a form or fetch() body in it
const MAX_URL_LENGTH = 8192;
const MAX_BODY_BYTES = 8 * 1024 * 1024;
const MAX_PAGE_BYTES = 16 * 1024 * 1024;
const MAX_RESOURCES = 60;
const MAX_RESOURCE_DEPTH = 3;
const MAX_BUNDLE_BYTES = 8 * 1024 * 1024;
const REQUEST_TIME_MS = 22000;
const BUNDLE_CONCURRENCY = 6;

const PAGE_METHODS = new Set(['GET', 'POST']);
const RESOURCE_METHODS = new Set(['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS']);

// Request headers a page's scripts may not set (as in browsers), that HazeGate sets itself, or that would say where
// the request really comes from.
const FORBIDDEN_HEADERS = new Set([
  'accept-charset', 'accept-encoding', 'access-control-request-headers', 'access-control-request-method', 'connection',
  'content-length', 'cookie', 'cookie2', 'date', 'dnt', 'expect', 'host', 'keep-alive', 'origin', 'referer', 'te',
  'trailer', 'transfer-encoding', 'upgrade', 'user-agent', 'via', 'content-type', 'authorization', 'forwarded',
  'x-forwarded-for', 'x-forwarded-host', 'x-forwarded-proto', 'x-real-ip', 'x-client-ip', 'x-functions-key',
  'accept', 'accept-language', 'proxy-authorization', 'x-api-key',
]);

// Response headers that describe the answer as it travelled, not the file (HazeGate decodes and re-sends it).
const DROPPED_RESPONSE_HEADERS = new Set([
  'connection', 'keep-alive', 'transfer-encoding', 'content-encoding', 'content-length', 'proxy-authenticate',
  'proxy-connection', 'trailer', 'upgrade', 'alt-svc', 'strict-transport-security', 'set-cookie',
]);

const ACCEPT_STYLE = 'text/css,*/*;q=0.1';
const ACCEPT_SCRIPT = '*/*';

/** An answer to PhantomHaze: a status and a JSON body. */
class Answer {
  constructor(status, json, host = '') {
    this.status = status;
    this.json = json;
    this.host = host; // for the log only
  }
}

function fail(status, message) {
  return new Answer(status, { v: PROTOCOL_VERSION, error: message });
}

function describeFetchError(error) {
  if (error instanceof FetchError) {
    const status = error.code === 'blocked-address' || error.code === 'blocked-port' || error.code === 'bad-url' ? 403 : 502;
    return { status, message: error.message };
  }

  const code = error && error.code;
  const messages = {
    ENOTFOUND: 'The server could not be found. Check the address.',
    EAI_AGAIN: 'The server could not be found right now. Try again.',
    ECONNREFUSED: 'The server refused the connection.',
    ECONNRESET: 'The connection to the server was interrupted.',
    EHOSTUNREACH: 'The server could not be reached.',
    ENETUNREACH: 'The server could not be reached.',
    ETIMEDOUT: 'The server took too long to respond.',
    EPROTO: 'A secure connection to the server could not be made.',
    HPE_HEADER_OVERFLOW: 'The server sent headers that are too large.',
  };
  if (code && messages[code]) {
    return { status: 502, message: messages[code] };
  }

  if (code && (String(code).startsWith('ERR_TLS') || String(code).includes('CERT') || String(code).startsWith('ERR_SSL'))) {
    return { status: 502, message: 'A secure connection to the server could not be made (its certificate is not valid).' };
  }

  return { status: 502, message: 'The page could not be downloaded.' };
}

/** The Referer browsers send by default (strict-origin-when-cross-origin), or null. */
function refererFor(referrer, target) {
  if (typeof referrer !== 'string' || referrer === '') {
    return null;
  }

  try {
    const from = new URL(referrer);
    if (from.protocol !== 'http:' && from.protocol !== 'https:') {
      return null;
    }

    if (from.protocol === 'https:' && target.protocol !== 'https:') {
      return null;
    }

    if (from.origin === target.origin) {
      from.hash = '';
      from.username = '';
      from.password = '';
      return from.href;
    }

    return `${from.origin}/`;
  } catch {
    return null;
  }
}

function headerValue(value) {
  return typeof value === 'string' && value.length < 8000 && !/[\r\n\0]/.test(value) ? value : null;
}

/** The headers HazeGate sends to the site. */
function buildHeaders(request, target, { accept, cookie }) {
  const headers = {
    'user-agent': headerValue(request.userAgent) || 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) HazeGate',
    accept: headerValue(accept) || '*/*',
    'accept-language': headerValue(request.acceptLanguage) || 'en-US,en;q=0.9',
    'accept-encoding': 'gzip, deflate, br',
  };
  const referer = refererFor(request.referrer, target);
  if (referer) {
    headers.referer = referer;
  }

  if (cookie && headerValue(cookie)) {
    headers.cookie = cookie;
  }

  if (request.headers && typeof request.headers === 'object' && !Array.isArray(request.headers)) {
    for (const [name, value] of Object.entries(request.headers).slice(0, 50)) {
      const lower = name.toLowerCase();
      if (/^[!#$%&'*+.^_`|~0-9a-z-]{1,100}$/i.test(name) && !FORBIDDEN_HEADERS.has(lower) &&
          !lower.startsWith('sec-') && !lower.startsWith('proxy-') && !lower.startsWith('x-ms-') && !lower.startsWith('x-amz') && headerValue(value) !== null) {
        headers[lower] = value;
      }
    }
  }

  return headers;
}

/** A body in the answer: text when it is valid UTF-8 (smaller than Base64), Base64 otherwise. */
function encodeBody(target, buffer) {
  if (buffer.length === 0) {
    target.text = '';
  } else if (isUtf8(buffer)) {
    target.text = buffer.toString('utf8');
  } else {
    target.body = buffer.toString('base64');
  }

  return target;
}

function isHtml(contentType, body) {
  const type = String(contentType).split(';')[0].trim().toLowerCase();
  if (type === 'text/html' || type === 'application/xhtml+xml') {
    return true;
  }

  if (type !== '') {
    return false;
  }

  const start = body.subarray(0, 512).toString('latin1').replace(/^[\s﻿ï»¿]+/, '');
  return start.startsWith('<');
}

/** Runs worker for every item, at most `concurrency` at a time; workers can add more items. */
function runPool(items, concurrency, worker) {
  const queue = [...items];
  let running = 0;
  return new Promise((resolve) => {
    const next = () => {
      while (running < concurrency && queue.length > 0) {
        const item = queue.shift();
        running++;
        Promise.resolve()
          .then(() => worker(item, (more) => queue.push(...more)))
          .catch(() => {})
          .finally(() => {
            running--;
            next();
          });
      }

      if (running === 0 && queue.length === 0) {
        resolve();
      }
    };
    next();
  });
}

/** Downloads the stylesheets and scripts of a page (and what they import), within the bundle's limits. */
async function collectResources(html, pageUrl, request, options, deadline) {
  const seen = new Set();
  const resources = [];
  let bundleBytes = 0;
  const initial = findPageResources(html, pageUrl)
    .filter((item) => {
      if (seen.has(item.url)) {
        return false;
      }

      seen.add(item.url);
      return true;
    })
    .slice(0, MAX_RESOURCES)
    .map((item) => ({ ...item, depth: 0, referrer: pageUrl }));

  await runPool(initial, BUNDLE_CONCURRENCY, async (item, enqueue) => {
    const entry = { url: item.url };
    resources.push(entry);
    if (Date.now() >= deadline || bundleBytes >= MAX_BUNDLE_BYTES) {
      entry.error = 'skipped';
      return;
    }

    let answer;
    try {
      const target = checkUrl(item.url, options);
      answer = await fetchFollowing(target, {
        headers: buildHeaders({ ...request, referrer: item.referrer, headers: null }, target, {
          accept: item.kind === 'style' ? ACCEPT_STYLE : ACCEPT_SCRIPT,
          cookie: null,
        }),
        maxBytes: Math.min(MAX_BODY_BYTES, MAX_BUNDLE_BYTES - bundleBytes),
        deadline,
        timeoutMs: 15000,
        allowPrivateNetworks: options.allowPrivateNetworks,
      });
    } catch (error) {
      entry.error = describeFetchError(error).message;
      return;
    }

    bundleBytes += answer.body.length;
    entry.finalUrl = answer.finalUrl;
    entry.redirects = answer.redirects;
    entry.status = answer.status;
    entry.contentType = answer.contentType;
    encodeBody(entry, answer.body);

    // What the stylesheet or module imports is needed as well.
    if (answer.status >= 200 && answer.status < 300 && item.depth < MAX_RESOURCE_DEPTH) {
      const text = answer.body.toString('utf8');
      const references = item.kind === 'style' ? findCssImports(text) : item.kind === 'module' ? findModuleImports(text) : [];
      const more = [];
      for (const reference of references) {
        let url;
        try {
          url = new URL(reference, answer.finalUrl);
          url.hash = '';
        } catch {
          continue;
        }

        if ((url.protocol === 'http:' || url.protocol === 'https:') && !seen.has(url.href) && seen.size < MAX_RESOURCES) {
          seen.add(url.href);
          more.push({ url: url.href, kind: item.kind, depth: item.depth + 1, referrer: answer.finalUrl });
        }
      }

      enqueue(more);
    }
  });

  return resources;
}

function readJson(body) {
  try {
    const value = JSON.parse(body.toString('utf8'));
    return value && typeof value === 'object' && !Array.isArray(value) ? value : null;
  } catch {
    return null;
  }
}

/** Handles one request after the Lambda adapter checks its bearer token. */
async function handleRequest({ method, body }, options = {}) {
  if (method === 'GET') {
    return new Answer(200, { service: 'HazeGate', version, protocol: PROTOCOL_VERSION });
  }

  if (method !== 'POST') {
    return fail(405, 'HazeGate accepts GET (a status check) and POST requests.');
  }

  if (body.length > MAX_REQUEST_BYTES) {
    return fail(413, 'The request is too large.');
  }

  const request = readJson(body);
  if (request === null) {
    return fail(400, 'The request must be a JSON object.');
  }

  if (request.v !== undefined && request.v !== PROTOCOL_VERSION) {
    return fail(400, `This HazeGate function speaks version ${PROTOCOL_VERSION} of the HazeGate protocol. Update PhantomHaze or the function.`);
  }

  const mode = request.mode === 'resource' ? 'resource' : request.mode === 'page' ? 'page' : null;
  if (mode === null) {
    return fail(400, 'The request needs a mode ("page" or "resource").');
  }

  if (typeof request.url !== 'string' || request.url.length === 0 || request.url.length > MAX_URL_LENGTH) {
    return fail(400, 'The request needs a web address.');
  }

  const httpMethod = typeof request.method === 'string' ? request.method.toUpperCase() : 'GET';
  if (!(mode === 'page' ? PAGE_METHODS : RESOURCE_METHODS).has(httpMethod)) {
    return fail(400, `HazeGate does not make ${String(request.method).slice(0, 20)} requests.`);
  }

  let requestBody = null;
  if (request.body !== undefined && request.body !== null) {
    if (typeof request.body !== 'string' || !/^[A-Za-z0-9+/]*={0,2}$/.test(request.body)) {
      return fail(400, 'The request body must be Base64.');
    }

    requestBody = Buffer.from(request.body, 'base64');
    if (requestBody.length > MAX_BODY_BYTES) {
      return fail(413, 'The request body is too large.');
    }
  }

  let target;
  try {
    target = checkUrl(request.url, options);
  } catch (error) {
    const { status, message } = describeFetchError(error);
    return fail(status, message);
  }

  const maxBytes = Number.isInteger(request.maxBytes) && request.maxBytes > 0
    ? Math.min(request.maxBytes, MAX_PAGE_BYTES)
    : mode === 'page' ? 10 * 1024 * 1024 : MAX_BODY_BYTES;
  const headers = buildHeaders(request, target, {
    accept: request.accept,
    cookie: typeof request.cookie === 'string' ? request.cookie : null,
  });
  if (requestBody !== null && httpMethod !== 'GET' && httpMethod !== 'HEAD') {
    headers['content-type'] = headerValue(request.contentType) || (mode === 'page' ? 'application/x-www-form-urlencoded' : 'text/plain;charset=UTF-8');
  }

  const deadline = Date.now() + REQUEST_TIME_MS;
  let answer;
  try {
    answer = await fetchOnce(target, {
      method: httpMethod,
      headers,
      body: httpMethod === 'GET' || httpMethod === 'HEAD' ? null : requestBody,
      maxBytes,
      timeoutMs: 20000,
      allowPrivateNetworks: options.allowPrivateNetworks,
    });
  } catch (error) {
    const { status, message } = describeFetchError(error);
    return Object.assign(fail(status, message), { host: target.host });
  }

  const json = {
    v: PROTOCOL_VERSION,
    url: answer.url,
    status: answer.status,
    statusText: answer.statusText,
    contentType: answer.contentType,
    location: answer.location,
    setCookies: answer.setCookies,
    headers: answer.headers.filter(([name]) => !DROPPED_RESPONSE_HEADERS.has(name.toLowerCase())),
  };
  encodeBody(json, answer.body);

  if (mode === 'page' && request.bundle === true && answer.status >= 200 && answer.status < 300 && isHtml(answer.contentType, answer.body)) {
    json.resources = await collectResources(answer.body.toString('utf8'), answer.url, request, options, deadline);
  } else {
    json.resources = [];
  }

  return new Answer(200, json, target.host);
}

/** The HTTP answer for an Answer: JSON, compressed with gzip when the caller accepts it. */
function toHttpResponse(answer, acceptEncoding) {
  const payload = Buffer.from(JSON.stringify(answer.json), 'utf8');
  const headers = {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff',
  };
  if (/\bgzip\b/i.test(String(acceptEncoding || '')) && payload.length > 1024) {
    headers['content-encoding'] = 'gzip';
    return { status: answer.status, headers, body: zlib.gzipSync(payload, { level: 6 }) };
  }

  return { status: answer.status, headers, body: payload };
}

module.exports = {
  handleRequest,
  toHttpResponse,
  buildHeaders,
  refererFor,
  PROTOCOL_VERSION,
  MAX_REQUEST_BYTES,
};
