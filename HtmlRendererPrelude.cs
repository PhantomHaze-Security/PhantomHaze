namespace PhantomHaze;

/// <summary>
/// The first script of every HTMLRenderer/HazeGate page. AngleSharp.Js provides the DOM; this adds the browser parts it
/// does not have or that PhantomHaze must control: a real navigator, window === the global object, timers that run in
/// virtual time (driven by WebPageSession), localStorage/sessionStorage, fetch and XMLHttpRequest (downloaded by the
/// protocol, with web.block applied), matchMedia, observers, history.pushState, window.open (a protected tab) and
/// dialogs (never shown: alert/confirm/prompt would be windows outside capture protection).
/// </summary>
internal static class PagePrelude
{
    public const string Script = """
(function (g, cfg) {
  'use strict';
  var bridge = {
    fetch: g.__phFetch, media: g.__phMatchMedia, open: g.__phOpen, url: g.__phUrl, go: g.__phGo,
    sget: g.__phStorageGet, sset: g.__phStorageSet, sdel: g.__phStorageRemove, sclear: g.__phStorageClear,
    skeys: g.__phStorageKeys, log: g.__phLog
  };
  ['__phFetch', '__phMatchMedia', '__phOpen', '__phUrl', '__phGo', '__phStorageGet', '__phStorageSet',
   '__phStorageRemove', '__phStorageClear', '__phStorageKeys', '__phLog'].forEach(function (name) {
    try { delete g[name]; } catch (e) { try { g[name] = undefined; } catch (e2) { } }
  });
  function noop() { }
  function define(target, name, value) {
    if (!target) { return; }
    try { Object.defineProperty(target, name, { value: value, configurable: true, writable: true, enumerable: false }); }
    catch (e) { try { target[name] = value; } catch (e2) { } }
  }
  function report(error) {
    try { bridge.log('error', String(error && (error.stack || error.message) || error)); } catch (e) { }
  }
  var originalWindow = g.window;

  // window, self, top and parent are all the global object, as in a browser.
  ['window', 'self', 'globalThis', 'top', 'parent', 'frames'].forEach(function (name) { define(g, name, g); });
  define(g, 'opener', null);
  define(g, 'frameElement', null);
  define(g, 'length', 0);
  define(g, 'name', '');
  define(g, 'closed', false);

  var nav = {
    userAgent: cfg.ua, appVersion: cfg.ua.replace(/^Mozilla\//, ''), appName: 'Netscape', appCodeName: 'Mozilla',
    product: 'Gecko', productSub: '20030107', vendor: 'Google Inc.', vendorSub: '', platform: 'Win32', oscpu: undefined,
    language: cfg.lang, languages: [cfg.lang, cfg.lang.split('-')[0]], cookieEnabled: true, onLine: true,
    doNotTrack: '1', globalPrivacyControl: true, hardwareConcurrency: 4, deviceMemory: 8, maxTouchPoints: 0,
    pdfViewerEnabled: false, webdriver: false, plugins: [], mimeTypes: [],
    javaEnabled: function () { return false; },
    sendBeacon: function () { return true; },
    vibrate: function () { return false; },
    clipboard: { writeText: function () { return Promise.resolve(); }, readText: function () { return Promise.resolve(''); } },
    permissions: { query: function () { return Promise.resolve({ state: 'denied', onchange: null, addEventListener: noop }); } },
    connection: { effectiveType: '4g', downlink: 10, rtt: 50, saveData: false, addEventListener: noop, removeEventListener: noop },
    geolocation: {
      getCurrentPosition: function (ok, fail) { if (fail) { setTimeout(function () { fail({ code: 1, message: 'User denied Geolocation' }); }, 0); } },
      watchPosition: function () { return 0; }, clearWatch: noop
    },
    storage: { estimate: function () { return Promise.resolve({ quota: 5242880, usage: 0 }); }, persist: function () { return Promise.resolve(false); } },
    locks: undefined, serviceWorker: undefined, mediaDevices: undefined, userAgentData: undefined
  };
  define(g, 'navigator', nav);
  define(g, 'clientInformation', nav);
  if (originalWindow && originalWindow !== g) { define(originalWindow, 'navigator', nav); }

  // The page area (there is no layout engine behind element sizes, so these are what scripts can rely on).
  var metrics = {
    innerWidth: cfg.width, innerHeight: cfg.height, outerWidth: cfg.width, outerHeight: cfg.height + 80,
    devicePixelRatio: cfg.dpr, screenX: 0, screenY: 0, screenLeft: 0, screenTop: 0, scrollX: 0, scrollY: 0,
    pageXOffset: 0, pageYOffset: 0
  };
  Object.keys(metrics).forEach(function (name) { define(g, name, metrics[name]); });
  ['scrollTo', 'scroll', 'scrollBy', 'focus', 'blur', 'print', 'stop', 'moveTo', 'resizeTo'].forEach(function (name) { define(g, name, noop); });
  try {
    if (g.Element && g.Element.prototype) {
      var ep = g.Element.prototype;
      if (!ep.scrollIntoView) { define(ep, 'scrollIntoView', noop); }
      if (!ep.scrollTo) { define(ep, 'scrollTo', noop); }
      if (!ep.scrollBy) { define(ep, 'scrollBy', noop); }
      if (!ep.getBoundingClientRect) {
        define(ep, 'getBoundingClientRect', function () {
          return { x: 0, y: 0, top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, toJSON: function () { return this; } };
        });
      }
      if (!ep.getClientRects) { define(ep, 'getClientRects', function () { return []; }); }
      if (!ep.animate) { define(ep, 'animate', function () { return { finished: Promise.resolve(), cancel: noop, play: noop, pause: noop, finish: noop, onfinish: null, addEventListener: noop }; }); }
    }
  } catch (e) { }

  // Timers run in virtual time: WebPageSession calls __phPump, which runs the next timer that is due before a limit.
  var timers = [];
  var nextTimer = 1;
  var timerSequence = 0;
  var virtualNow = 0;
  var realDateNow = Date.now.bind(Date);
  var realStart = realDateNow();
  function addTimer(fn, delay, args, repeat) {
    if (typeof fn !== 'function') {
      var code = String(fn);
      fn = function () { (0, eval)(code); };
    }
    var wait = Math.max(0, Number(delay) || 0);
    var id = nextTimer++;
    timers.push({ id: id, fn: fn, args: args, due: virtualNow + wait, interval: repeat ? Math.max(wait, 4) : -1, seq: timerSequence++ });
    return id;
  }
  function clearTimer(id) {
    for (var i = 0; i < timers.length; i++) { if (timers[i].id === id) { timers.splice(i, 1); return; } }
  }
  function slice(list, from) { return Array.prototype.slice.call(list, from); }
  function performanceNow() { return (realDateNow() - realStart) + virtualNow; }
  define(g, 'setTimeout', function (fn, delay) { return addTimer(fn, delay, slice(arguments, 2), false); });
  define(g, 'setInterval', function (fn, delay) { return addTimer(fn, delay, slice(arguments, 2), true); });
  define(g, 'clearTimeout', clearTimer);
  define(g, 'clearInterval', clearTimer);
  define(g, 'setImmediate', function (fn) { return addTimer(fn, 0, slice(arguments, 1), false); });
  define(g, 'clearImmediate', clearTimer);
  define(g, 'requestAnimationFrame', function (callback) { return addTimer(function () { callback(performanceNow()); }, 16, [], false); });
  define(g, 'cancelAnimationFrame', clearTimer);
  define(g, 'requestIdleCallback', function (callback) {
    return addTimer(function () { callback({ didTimeout: false, timeRemaining: function () { return 40; } }); }, 1, [], false);
  });
  define(g, 'cancelIdleCallback', clearTimer);
  define(g, 'queueMicrotask', function (fn) { Promise.resolve().then(function () { try { fn(); } catch (e) { report(e); } }); });
  try { Date.now = function () { return realDateNow() + virtualNow; }; } catch (e) { }
  if (g.performance) {
    define(g.performance, 'now', performanceNow);
  } else {
    define(g, 'performance', {
      now: performanceNow, timeOrigin: realStart, mark: noop, measure: noop, clearMarks: noop, clearMeasures: noop,
      getEntries: function () { return []; }, getEntriesByType: function () { return []; }, getEntriesByName: function () { return []; }
    });
  }
  define(g, '__phNow', function () { return virtualNow; });
  define(g, '__phPump', function (limit) {
    if (!timers.length) { return 0; }
    var best = 0;
    for (var i = 1; i < timers.length; i++) {
      var t = timers[i], b = timers[best];
      if (t.due < b.due || (t.due === b.due && t.seq < b.seq)) { best = i; }
    }
    var timer = timers[best];
    if (timer.due > limit) { return 0; }
    if (timer.due > virtualNow) { virtualNow = timer.due; }
    if (timer.interval >= 0) { timer.due = virtualNow + timer.interval; timer.seq = timerSequence++; }
    else { timers.splice(best, 1); }
    try { timer.fn.apply(g, timer.args); } catch (e) { report(e); }
    return 1;
  });

  // localStorage and sessionStorage (kept by PhantomHaze in memory, per site, for the session).
  function makeStorage(area) {
    var api = {
      getItem: function (key) { var value = bridge.sget(area, String(key)); return value === undefined ? null : value; },
      setItem: function (key, value) { bridge.sset(area, String(key), String(value)); },
      removeItem: function (key) { bridge.sdel(area, String(key)); },
      clear: function () { bridge.sclear(area); },
      key: function (index) { var keys = JSON.parse(bridge.skeys(area)); return index >= 0 && index < keys.length ? keys[index] : null; }
    };
    Object.defineProperty(api, 'length', { get: function () { return JSON.parse(bridge.skeys(area)).length; } });
    if (typeof Proxy !== 'function') { return api; }
    return new Proxy(api, {
      get: function (target, name) {
        if (typeof name !== 'string' || name in target) { return target[name]; }
        return target.getItem(name);
      },
      set: function (target, name, value) { if (typeof name === 'string') { target.setItem(name, value); } return true; },
      deleteProperty: function (target, name) { if (typeof name === 'string') { target.removeItem(name); } return true; },
      has: function (target, name) { return name in target || (typeof name === 'string' && target.getItem(name) !== null); }
    });
  }
  define(g, 'localStorage', makeStorage('local'));
  define(g, 'sessionStorage', makeStorage('session'));

  // fetch() and XMLHttpRequest, downloaded by the protocol (cookies only go to the page's own site).
  var pending = {};
  var nextRequest = 1;
  define(g, '__phFetchDone', function (id, status, statusText, url, headers, body, kind, error) {
    var done = pending[id];
    delete pending[id];
    if (done) {
      try { done({ status: status, statusText: statusText, url: url, headers: JSON.parse(headers || '{}'), body: body, kind: kind, error: error }); }
      catch (e) { report(e); }
    }
  });
  function startRequest(method, url, headers, body, mode, done) {
    var id = nextRequest++;
    pending[id] = done;
    try { bridge.fetch(id, String(url), String(method || 'GET'), JSON.stringify(headers || {}), body == null ? '' : String(body), String(mode || 'same-origin')); }
    catch (e) { delete pending[id]; done({ status: 0, statusText: '', url: String(url), headers: {}, body: '', kind: 'text', error: String(e) }); }
  }
  function decodeBase64(text) {
    var binary = atob(text || '');
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) { bytes[i] = binary.charCodeAt(i); }
    return bytes;
  }
  function bodyText(result) {
    if (result.kind !== 'base64') { return result.body || ''; }
    var bytes = decodeBase64(result.body);
    if (typeof TextDecoder === 'function') { try { return new TextDecoder('utf-8').decode(bytes); } catch (e) { } }
    var text = '';
    for (var i = 0; i < bytes.length; i++) { text += String.fromCharCode(bytes[i]); }
    return text;
  }
  function bodyBytes(result) {
    if (result.kind === 'base64') { return decodeBase64(result.body); }
    if (typeof TextEncoder === 'function') { return new TextEncoder().encode(result.body || ''); }
    var text = result.body || '';
    var bytes = new Uint8Array(text.length);
    for (var i = 0; i < text.length; i++) { bytes[i] = text.charCodeAt(i) & 255; }
    return bytes;
  }
  function serializeBody(body) {
    if (body == null) { return null; }
    if (typeof body === 'string') { return body; }
    if (typeof URLSearchParams === 'function' && body instanceof URLSearchParams) { return body.toString(); }
    if (body && typeof body.__phEntries === 'function') {
      return body.__phEntries().map(function (pair) { return encodeURIComponent(pair[0]) + '=' + encodeURIComponent(pair[1]); }).join('&');
    }
    if (typeof body === 'object' && typeof body.byteLength === 'number') {
      var bytes = new Uint8Array(body.buffer || body), text = '';
      for (var i = 0; i < bytes.length; i++) { text += String.fromCharCode(bytes[i]); }
      return text;
    }
    return String(body);
  }
  function PhHeaders(init) {
    var map = {};
    this._map = map;
    if (init) {
      if (typeof init.forEach === 'function' && !Array.isArray(init)) { init.forEach(function (value, key) { map[String(key).toLowerCase()] = String(value); }); }
      else if (Array.isArray(init)) { init.forEach(function (pair) { map[String(pair[0]).toLowerCase()] = String(pair[1]); }); }
      else { Object.keys(init).forEach(function (key) { map[key.toLowerCase()] = String(init[key]); }); }
    }
  }
  PhHeaders.prototype.get = function (name) { var value = this._map[String(name).toLowerCase()]; return value === undefined ? null : value; };
  PhHeaders.prototype.has = function (name) { return String(name).toLowerCase() in this._map; };
  PhHeaders.prototype.set = function (name, value) { this._map[String(name).toLowerCase()] = String(value); };
  PhHeaders.prototype.append = function (name, value) {
    var key = String(name).toLowerCase();
    this._map[key] = key in this._map ? this._map[key] + ', ' + value : String(value);
  };
  PhHeaders.prototype['delete'] = function (name) { delete this._map[String(name).toLowerCase()]; };
  PhHeaders.prototype.forEach = function (callback, self) { var map = this._map; Object.keys(map).forEach(function (key) { callback.call(self, map[key], key); }); };
  PhHeaders.prototype.entries = function () { var map = this._map; return Object.keys(map).map(function (key) { return [key, map[key]]; })[Symbol.iterator](); };
  PhHeaders.prototype.keys = function () { return Object.keys(this._map)[Symbol.iterator](); };
  PhHeaders.prototype.values = function () { var map = this._map; return Object.keys(map).map(function (key) { return map[key]; })[Symbol.iterator](); };
  PhHeaders.prototype[Symbol.iterator] = PhHeaders.prototype.entries;
  function PhResponse(result) {
    this.status = result.status;
    this.statusText = result.statusText || '';
    this.ok = result.status >= 200 && result.status < 300;
    this.url = result.url;
    this.redirected = false;
    this.type = 'basic';
    this.headers = new PhHeaders(result.headers);
    this.bodyUsed = false;
    this._result = result;
  }
  PhResponse.prototype.text = function () { this.bodyUsed = true; return Promise.resolve(bodyText(this._result)); };
  PhResponse.prototype.json = function () {
    this.bodyUsed = true;
    var self = this;
    return new Promise(function (resolve, reject) { try { resolve(JSON.parse(bodyText(self._result))); } catch (e) { reject(e); } });
  };
  PhResponse.prototype.arrayBuffer = function () { this.bodyUsed = true; return Promise.resolve(bodyBytes(this._result).buffer); };
  PhResponse.prototype.blob = function () {
    this.bodyUsed = true;
    var bytes = bodyBytes(this._result);
    var type = this.headers.get('content-type') || '';
    return Promise.resolve(typeof Blob === 'function' ? new Blob([bytes], { type: type }) : { size: bytes.length, type: type, text: function () { return Promise.resolve(bodyText(this._result)); } });
  };
  PhResponse.prototype.formData = function () { return Promise.reject(new TypeError('formData() is not supported')); };
  PhResponse.prototype.clone = function () { return new PhResponse(this._result); };
  define(g, 'Headers', PhHeaders);
  define(g, 'Response', PhResponse);
  define(g, 'fetch', function (input, init) {
    init = init || {};
    var url = typeof input === 'string' ? input : (input && (input.href || input.url)) || String(input);
    var method = (init.method || (input && input.method) || 'GET').toUpperCase();
    var headers = new PhHeaders(init.headers || (input && input.headers));
    var body = serializeBody(init.body !== undefined ? init.body : (input && input.body));
    if (body !== null && !headers.has('content-type') && init.body && typeof init.body === 'string') {
      headers.set('content-type', 'text/plain;charset=UTF-8');
    }
    if (typeof URLSearchParams === 'function' && init.body instanceof URLSearchParams && !headers.has('content-type')) {
      headers.set('content-type', 'application/x-www-form-urlencoded;charset=UTF-8');
    }
    var signal = init.signal;
    return new Promise(function (resolve, reject) {
      if (signal && signal.aborted) { reject(new DOMException('The operation was aborted.', 'AbortError')); return; }
      startRequest(method, url, headers._map, body, init.credentials === 'omit' ? 'omit' : 'same-origin', function (result) {
        if (signal && signal.aborted) { reject(new DOMException('The operation was aborted.', 'AbortError')); return; }
        if (result.error || result.status === 0) { reject(new TypeError('Failed to fetch')); return; }
        resolve(new PhResponse(result));
      });
    });
  });
  if (typeof g.AbortController !== 'function') {
    var PhAbortController = function () {
      var listeners = [];
      this.signal = {
        aborted: false, reason: undefined, onabort: null,
        addEventListener: function (type, fn) { if (type === 'abort') { listeners.push(fn); } },
        removeEventListener: function (type, fn) { listeners = listeners.filter(function (l) { return l !== fn; }); },
        throwIfAborted: function () { if (this.aborted) { throw this.reason; } }
      };
      var signal = this.signal;
      this.abort = function (reason) {
        if (signal.aborted) { return; }
        signal.aborted = true;
        signal.reason = reason;
        listeners.concat(signal.onabort ? [signal.onabort] : []).forEach(function (fn) { try { fn({ type: 'abort', target: signal }); } catch (e) { report(e); } });
      };
    };
    define(g, 'AbortController', PhAbortController);
  }

  function PhXMLHttpRequest() {
    this.readyState = 0; this.status = 0; this.statusText = ''; this.responseText = ''; this.responseXML = null;
    this.response = ''; this.responseType = ''; this.responseURL = ''; this.timeout = 0; this.withCredentials = false;
    this.onreadystatechange = null; this.onload = null; this.onerror = null; this.onloadend = null; this.onloadstart = null;
    this.onabort = null; this.ontimeout = null; this.onprogress = null;
    this.upload = { addEventListener: noop, removeEventListener: noop, onprogress: null };
    this._headers = {}; this._responseHeaders = {}; this._listeners = {}; this._aborted = false;
  }
  PhXMLHttpRequest.UNSENT = 0; PhXMLHttpRequest.OPENED = 1; PhXMLHttpRequest.HEADERS_RECEIVED = 2;
  PhXMLHttpRequest.LOADING = 3; PhXMLHttpRequest.DONE = 4;
  var xp = PhXMLHttpRequest.prototype;
  xp.UNSENT = 0; xp.OPENED = 1; xp.HEADERS_RECEIVED = 2; xp.LOADING = 3; xp.DONE = 4;
  xp.addEventListener = function (type, fn) { (this._listeners[type] = this._listeners[type] || []).push(fn); };
  xp.removeEventListener = function (type, fn) { var list = this._listeners[type] || []; this._listeners[type] = list.filter(function (l) { return l !== fn; }); };
  xp._emit = function (type) {
    var event = { type: type, target: this, currentTarget: this, loaded: 0, total: 0, lengthComputable: false };
    var handler = this['on' + type];
    try { if (typeof handler === 'function') { handler.call(this, event); } } catch (e) { report(e); }
    var list = (this._listeners[type] || []).slice();
    for (var i = 0; i < list.length; i++) { try { list[i].call(this, event); } catch (e) { report(e); } }
  };
  xp.open = function (method, url) {
    this._method = String(method || 'GET').toUpperCase(); this._url = String(url); this.readyState = 1; this._aborted = false;
    this._emit('readystatechange');
  };
  xp.setRequestHeader = function (name, value) { this._headers[String(name).toLowerCase()] = String(value); };
  xp.getResponseHeader = function (name) { var value = this._responseHeaders[String(name).toLowerCase()]; return value === undefined ? null : value; };
  xp.getAllResponseHeaders = function () {
    var map = this._responseHeaders;
    return Object.keys(map).map(function (key) { return key + ': ' + map[key]; }).join('\r\n');
  };
  xp.overrideMimeType = noop;
  xp.abort = function () { this._aborted = true; this.readyState = 0; this._emit('abort'); this._emit('loadend'); };
  xp.send = function (body) {
    var self = this;
    self._emit('loadstart');
    startRequest(self._method, self._url, self._headers, serializeBody(body), self.withCredentials ? 'include' : 'same-origin', function (result) {
      if (self._aborted) { return; }
      self.status = result.error ? 0 : result.status;
      self.statusText = result.statusText || '';
      self.responseURL = result.url;
      self._responseHeaders = result.headers || {};
      self.readyState = 2; self._emit('readystatechange');
      self.readyState = 3; self._emit('readystatechange');
      var text = bodyText(result);
      if (self.responseType === 'arraybuffer') { self.response = bodyBytes(result).buffer; }
      else if (self.responseType === 'json') { try { self.response = JSON.parse(text); } catch (e) { self.response = null; } }
      else if (self.responseType === 'blob') { self.response = typeof Blob === 'function' ? new Blob([bodyBytes(result)]) : text; }
      else if (self.responseType === 'document') {
        try { self.response = new DOMParser().parseFromString(text, 'text/html'); } catch (e) { self.response = null; }
      } else { self.responseText = text; self.response = text; }
      self.readyState = 4;
      self._emit('readystatechange');
      self._emit(result.error ? 'error' : 'load');
      self._emit('loadend');
    });
  };
  define(g, 'XMLHttpRequest', PhXMLHttpRequest);

  if (typeof g.FormData !== 'function') {
    var PhFormData = function (form) {
      var entries = [];
      this._entries = entries;
      if (form && form.elements) {
        for (var i = 0; i < form.elements.length; i++) {
          var field = form.elements[i];
          if (!field.name || field.disabled) { continue; }
          var type = (field.type || '').toLowerCase();
          if ((type === 'checkbox' || type === 'radio') && !field.checked) { continue; }
          if (type === 'submit' || type === 'button' || type === 'reset' || type === 'file') { continue; }
          entries.push([field.name, field.value == null ? '' : String(field.value)]);
        }
      }
    };
    var fp = PhFormData.prototype;
    fp.append = function (name, value) { this._entries.push([String(name), String(value)]); };
    fp.set = function (name, value) { this['delete'](name); this.append(name, value); };
    fp.get = function (name) { for (var i = 0; i < this._entries.length; i++) { if (this._entries[i][0] === String(name)) { return this._entries[i][1]; } } return null; };
    fp.getAll = function (name) { return this._entries.filter(function (e) { return e[0] === String(name); }).map(function (e) { return e[1]; }); };
    fp.has = function (name) { return this.get(name) !== null; };
    fp['delete'] = function (name) { this._entries = this._entries.filter(function (e) { return e[0] !== String(name); }); };
    fp.forEach = function (callback, self) { this._entries.forEach(function (e) { callback.call(self, e[1], e[0]); }); };
    fp.entries = function () { return this._entries.slice()[Symbol.iterator](); };
    fp.__phEntries = function () { return this._entries.slice(); };
    fp[Symbol.iterator] = fp.entries;
    define(g, 'FormData', PhFormData);
  }

  define(g, 'matchMedia', function (query) {
    var text = String(query);
    var matches = false;
    try { matches = !!bridge.media(text); } catch (e) { }
    return {
      matches: matches, media: text, onchange: null, addListener: noop, removeListener: noop,
      addEventListener: noop, removeEventListener: noop, dispatchEvent: function () { return false; }
    };
  });

  // Everything is "visible": there is no scrolling layout, so lazy content is loaded straight away.
  function rectFor() { return { x: 0, y: 0, top: 0, left: 0, right: cfg.width, bottom: 1, width: cfg.width, height: 1 }; }
  function PhIntersectionObserver(callback, options) {
    this._callback = callback; this.root = (options && options.root) || null; this.rootMargin = (options && options.rootMargin) || '0px'; this.thresholds = [0];
  }
  PhIntersectionObserver.prototype.observe = function (target) {
    var self = this;
    addTimer(function () {
      self._callback([{ target: target, isIntersecting: true, intersectionRatio: 1, time: performanceNow(),
        boundingClientRect: rectFor(), intersectionRect: rectFor(), rootBounds: rectFor() }], self);
    }, 0, [], false);
  };
  PhIntersectionObserver.prototype.unobserve = noop;
  PhIntersectionObserver.prototype.disconnect = noop;
  PhIntersectionObserver.prototype.takeRecords = function () { return []; };
  define(g, 'IntersectionObserver', PhIntersectionObserver);
  function PhResizeObserver(callback) { this._callback = callback; }
  PhResizeObserver.prototype.observe = function (target) {
    var self = this;
    addTimer(function () {
      var size = [{ inlineSize: cfg.width, blockSize: 1 }];
      self._callback([{ target: target, contentRect: rectFor(), borderBoxSize: size, contentBoxSize: size, devicePixelContentBoxSize: size }], self);
    }, 0, [], false);
  };
  PhResizeObserver.prototype.unobserve = noop;
  PhResizeObserver.prototype.disconnect = noop;
  define(g, 'ResizeObserver', PhResizeObserver);
  if (typeof g.MutationObserver !== 'function') {
    var PhMutationObserver = function () { };
    PhMutationObserver.prototype.observe = noop;
    PhMutationObserver.prototype.disconnect = noop;
    PhMutationObserver.prototype.takeRecords = function () { return []; };
    define(g, 'MutationObserver', PhMutationObserver);
  }

  // history.pushState changes the address PhantomHaze shows; back/forward use the tab's own history.
  var historyState = null;
  var historyEntries = 1;
  var phHistory = {
    scrollRestoration: 'auto',
    pushState: function (state, title, url) { historyState = state === undefined ? null : state; historyEntries++; if (url != null) { bridge.url(String(url), false); } },
    replaceState: function (state, title, url) { historyState = state === undefined ? null : state; if (url != null) { bridge.url(String(url), true); } },
    back: function () { bridge.go(-1); },
    forward: function () { bridge.go(1); },
    go: function (delta) { delta = delta | 0; if (delta) { bridge.go(delta); } }
  };
  Object.defineProperty(phHistory, 'state', { get: function () { return historyState; } });
  Object.defineProperty(phHistory, 'length', { get: function () { return historyEntries; } });
  define(g, 'history', phHistory);
  if (originalWindow && originalWindow !== g) { define(originalWindow, 'history', phHistory); }

  define(g, 'open', function (url) { if (url != null && String(url) !== '' && String(url) !== 'about:blank') { bridge.open(String(url)); } return null; });
  define(g, 'alert', function (message) { bridge.log('alert', String(message)); });
  define(g, 'confirm', function (message) { bridge.log('confirm', String(message)); return false; });
  define(g, 'prompt', function (message) { bridge.log('prompt', String(message)); return null; });
  define(g, 'showModalDialog', function () { return null; });

  if (!g.CSS || typeof g.CSS.supports !== 'function') {
    define(g, 'CSS', {
      supports: function (property, value) {
        var text = value === undefined ? String(property) : String(property) + ':' + String(value);
        return !/grid|subgrid|sticky|backdrop|container|aspect-ratio/i.test(text);
      },
      escape: function (text) { return String(text).replace(/([^a-zA-Z0-9_ -￿-])/g, '\\$1'); }
    });
  }
  if (typeof g.structuredClone !== 'function') {
    define(g, 'structuredClone', function (value) { return value === undefined ? undefined : JSON.parse(JSON.stringify(value)); });
  }
  try {
    if (g.document && !g.document.fonts) {
      define(g.document, 'fonts', { ready: Promise.resolve(), status: 'loaded', load: function () { return Promise.resolve([]); }, check: function () { return true; },
        add: noop, addEventListener: noop, removeEventListener: noop, forEach: noop, size: 0 });
    }
  } catch (e) { }
  try { if (g.document && typeof g.document.hasFocus !== 'function') { define(g.document, 'hasFocus', function () { return true; }); } } catch (e) { }
  try { if (g.document) { define(g.document, 'visibilityState', 'visible'); define(g.document, 'hidden', false); } } catch (e) { }

  // Elements that get click handlers are marked, so PhantomHaze makes them clickable in the drawn page.
  try {
    var targetPrototype = g.EventTarget && g.EventTarget.prototype;
    var originalAdd = targetPrototype && targetPrototype.addEventListener;
    if (typeof originalAdd === 'function') {
      define(targetPrototype, 'addEventListener', function (type, listener, options) {
        try {
          if (this && this.nodeType === 1 && /^(click|dblclick|mousedown|mouseup|pointerdown|pointerup|touchstart|touchend)$/.test(String(type))) {
            if (!this.hasAttribute('data-ph-click')) { this.setAttribute('data-ph-click', ''); }
          }
        } catch (e) { }
        return originalAdd.apply(this, arguments);
      });
    }
  } catch (e) { }

  try { g.addEventListener('error', function (event) { report(event && (event.error || event.message)); }); } catch (e) { }
  try { g.addEventListener('unhandledrejection', function (event) { report(event && event.reason); }); } catch (e) { }
})(this, __PH_CONFIG__);
""";
}
