'use strict';

// Downloads one address for HazeGate, safely:
// - only http:// and https://, never a port browsers refuse (addresses.js);
// - every address the host name resolves to is checked, and the connection is made to the checked address itself, so
//   a host name cannot point HazeGate at a private network between the check and the connection (DNS rebinding);
// - redirects are never followed here: they are returned to the caller, which checks the next address again;
// - answers are limited in size and time, and decompressed (gzip, deflate, br) with the same size limit.

const http = require('node:http');
const https = require('node:https');
const dns = require('node:dns');
const net = require('node:net');
const zlib = require('node:zlib');
const { isBlockedAddress, isBadPort } = require('./addresses');

const REDIRECT_STATUSES = new Set([301, 302, 303, 307, 308]);

/** A download that failed; code says why (handler.js turns it into a message for people). */
class FetchError extends Error {
  constructor(code, message) {
    super(message);
    this.name = 'FetchError';
    this.code = code;
  }
}

/** The address as a URL, after checking that HazeGate may open it (throws FetchError otherwise). */
function checkUrl(value, { allowPrivateNetworks = false } = {}) {
  let url;
  try {
    url = value instanceof URL ? value : new URL(String(value));
  } catch {
    throw new FetchError('bad-url', 'This is not a valid web address.');
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new FetchError('bad-url', 'HazeGate only opens http:// and https:// addresses.');
  }

  if (url.username || url.password) {
    throw new FetchError('bad-url', 'HazeGate does not open addresses that contain a user name or password.');
  }

  const port = url.port ? Number(url.port) : url.protocol === 'https:' ? 443 : 80;
  if (isBadPort(port)) {
    throw new FetchError('blocked-port', `HazeGate does not connect to port ${port}, which is not used for web pages.`);
  }

  // The URL parser has already turned numeric forms ("http://2130706433/") into a normal IP address.
  const host = url.hostname.replace(/^\[|\]$/g, '');
  if (!allowPrivateNetworks && net.isIP(host) && isBlockedAddress(host)) {
    throw new FetchError('blocked-address', 'HazeGate does not open addresses on private networks.');
  }

  return url;
}

/** A dns.lookup replacement for http.request that refuses host names with any address on a private network. */
function createLookup(allowPrivateNetworks) {
  return function lookup(hostname, options, callback) {
    if (typeof options === 'function') {
      callback = options;
      options = {};
    }

    dns.lookup(hostname, { all: true, verbatim: true }, (error, addresses) => {
      if (error) {
        callback(error);
        return;
      }

      if (!allowPrivateNetworks && addresses.some((entry) => isBlockedAddress(entry.address))) {
        callback(new FetchError('blocked-address', 'HazeGate does not open addresses on private networks.'));
        return;
      }

      let usable = addresses;
      if (options && (options.family === 4 || options.family === 6)) {
        const sameFamily = addresses.filter((entry) => entry.family === options.family);
        usable = sameFamily.length > 0 ? sameFamily : addresses;
      }

      if (usable.length === 0) {
        callback(Object.assign(new Error(`No address for ${hostname}`), { code: 'ENOTFOUND' }));
        return;
      }

      if (options && options.all) {
        callback(null, usable.map((entry) => ({ address: entry.address, family: entry.family })));
      } else {
        callback(null, usable[0].address, usable[0].family);
      }
    });
  };
}

/** The body without its content encoding (gzip, deflate or br), at most maxBytes long. */
function decodeBody(raw, encoding, maxBytes) {
  const options = { maxOutputLength: maxBytes };
  try {
    switch (String(encoding || '').trim().toLowerCase()) {
      case '':
      case 'identity':
        return raw;
      case 'gzip':
      case 'x-gzip':
        return zlib.gunzipSync(raw, options);
      case 'deflate':
        // Servers send "deflate" both with the zlib wrapper (as the standard says) and without it.
        try {
          return zlib.inflateSync(raw, options);
        } catch (error) {
          if (error instanceof RangeError) {
            throw error;
          }

          return zlib.inflateRawSync(raw, options);
        }
      case 'br':
        return zlib.brotliDecompressSync(raw, options);
      default:
        throw new FetchError('bad-response', `The server sent data in a format HazeGate cannot read (${encoding}).`);
    }
  } catch (error) {
    if (error instanceof FetchError) {
      throw error;
    }

    if (error instanceof RangeError || error.code === 'ERR_BUFFER_TOO_LARGE') {
      throw new FetchError('too-large', 'The file is larger than HazeGate downloads.');
    }

    throw new FetchError('bad-response', 'The server sent data that could not be read.');
  }
}

/**
 * Downloads one address without following redirects.
 * @returns {Promise<{url: string, status: number, statusText: string, headers: string[][], setCookies: string[],
 *   location: string|null, contentType: string, body: Buffer}>}
 */
function fetchOnce(target, options = {}) {
  const {
    method = 'GET',
    headers = {},
    body = null,
    maxBytes = 8 * 1024 * 1024,
    timeoutMs = 20000,
    allowPrivateNetworks = false,
  } = options;

  return new Promise((resolve, reject) => {
    let url;
    try {
      url = checkUrl(target, { allowPrivateNetworks });
    } catch (error) {
      reject(error);
      return;
    }

    const transport = url.protocol === 'https:' ? https : http;
    let settled = false;
    const finish = (error, result) => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(deadline);
      if (error) {
        reject(error);
      } else {
        resolve(result);
      }
    };

    let deadline;
    let request;
    try {
      request = transport.request(url, {
        method,
        headers,
        lookup: createLookup(allowPrivateNetworks),
        timeout: timeoutMs,
        maxHeaderSize: 64 * 1024,
        agent: false,
      });
    } catch (error) {
      // For example a header value with a line break.
      finish(new FetchError('bad-request', `The request could not be made: ${error.message}`));
      return;
    }

    deadline = setTimeout(() => {
      request.destroy(new FetchError('timeout', 'The server took too long to respond.'));
    }, timeoutMs);

    request.on('timeout', () => request.destroy(new FetchError('timeout', 'The server took too long to respond.')));
    request.on('error', (error) => finish(error));
    request.on('response', (response) => {
      const status = response.statusCode || 0;
      const pairs = [];
      const setCookies = [];
      for (let i = 0; i + 1 < response.rawHeaders.length; i += 2) {
        const name = response.rawHeaders[i];
        const value = response.rawHeaders[i + 1];
        if (name.toLowerCase() === 'set-cookie') {
          setCookies.push(value);
        } else {
          pairs.push([name, value]);
        }
      }

      let location = null;
      if (REDIRECT_STATUSES.has(status) && response.headers.location) {
        try {
          location = new URL(response.headers.location, url).href;
        } catch {
          location = null;
        }
      }

      const result = {
        url: url.href,
        status,
        statusText: response.statusMessage || '',
        headers: pairs,
        setCookies,
        location,
        contentType: String(response.headers['content-type'] || ''),
        body: Buffer.alloc(0),
      };

      const declared = Number(response.headers['content-length']);
      if (Number.isFinite(declared) && declared > maxBytes && !response.headers['content-encoding']) {
        // Settled first: destroying the response reports it as interrupted straight away.
        finish(new FetchError('too-large', 'The file is larger than HazeGate downloads.'));
        response.destroy();
        return;
      }

      if (method === 'HEAD' || status === 204 || status === 304 || location !== null) {
        response.resume();
        response.on('end', () => finish(null, result));
        response.on('error', () => finish(null, result));
        return;
      }

      const chunks = [];
      let size = 0;
      response.on('data', (chunk) => {
        size += chunk.length;
        if (size > maxBytes) {
          finish(new FetchError('too-large', 'The file is larger than HazeGate downloads.'));
          response.destroy();
          return;
        }

        chunks.push(chunk);
      });
      response.on('end', () => {
        try {
          result.body = decodeBody(Buffer.concat(chunks), response.headers['content-encoding'], maxBytes);
          finish(null, result);
        } catch (error) {
          finish(error);
        }
      });
      response.on('error', (error) => finish(error));
      response.on('aborted', () => finish(new FetchError('bad-response', 'The connection to the server was interrupted.')));
    });

    if (body && body.length > 0) {
      request.end(body);
    } else {
      request.end();
    }
  });
}

/**
 * Downloads an address, following up to maxRedirects redirects (each new address is checked again). A redirect from
 * https:// to http:// is refused, as browsers refuse it for the parts of a page.
 * @returns the answer of the last address, with finalUrl and redirects (every address that redirected).
 */
async function fetchFollowing(target, options = {}, maxRedirects = 5) {
  const redirects = [];
  let current = checkUrl(target, options);
  let { method = 'GET', body = null } = options;
  for (let step = 0; step <= maxRedirects; step++) {
    const remaining = options.deadline === undefined ? Infinity : options.deadline - Date.now();
    if (remaining <= 0) throw new FetchError('timeout', 'The page bundle took too long to download.');
    const timeoutMs = Math.min(options.timeoutMs ?? 20000, remaining);
    const answer = await fetchOnce(current, { ...options, method, body, timeoutMs });
    if (answer.location === null) {
      return { ...answer, finalUrl: current.href, redirects };
    }

    const next = checkUrl(answer.location, options);
    if (current.protocol === 'https:' && next.protocol !== 'https:') {
      throw new FetchError('insecure-redirect', 'An https:// address redirected to an insecure http:// address.');
    }

    if ([301, 302, 303].includes(answer.status) && method !== 'GET' && method !== 'HEAD') {
      method = 'GET';
      body = null;
    }

    redirects.push(current.href);
    current = next;
  }

  throw new FetchError('too-many-redirects', 'The address redirected too many times.');
}

module.exports = { fetchOnce, fetchFollowing, checkUrl, FetchError, REDIRECT_STATUSES };
