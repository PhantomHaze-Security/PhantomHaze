'use strict';

// Finds the stylesheets and scripts a page uses, so HazeGate can send them together with the page: <link
// rel="stylesheet">, <script src>, preloads, @import rules (in <style> elements and in stylesheets) and the static
// imports of JavaScript modules. It reads the markup with regular expressions rather than a full HTML parser: what it
// misses, PhantomHaze asks HazeGate for separately, so it only has to find the usual cases.

const SCRIPT_TYPES = new Set([
  '',
  'text/javascript',
  'application/javascript',
  'application/ecmascript',
  'application/x-javascript',
  'application/x-ecmascript',
  'text/ecmascript',
  'text/jscript',
  'text/livescript',
  'text/x-javascript',
  'text/x-ecmascript',
  'text/javascript1.0',
  'text/javascript1.1',
  'text/javascript1.2',
  'text/javascript1.3',
  'text/javascript1.4',
  'text/javascript1.5',
]);

const ATTRIBUTE = /([^\s"'<>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g;

const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

function decodeEntities(value) {
  return value.replace(/&(#x[0-9a-f]+|#[0-9]+|[a-z]+);?/gi, (match, name) => {
    if (name[0] === '#') {
      const code = name[1] === 'x' || name[1] === 'X' ? parseInt(name.slice(2), 16) : parseInt(name.slice(1), 10);
      return Number.isInteger(code) && code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
    }

    return Object.prototype.hasOwnProperty.call(ENTITIES, name.toLowerCase()) ? ENTITIES[name.toLowerCase()] : match;
  });
}

function parseAttributes(text) {
  const attributes = new Map();
  ATTRIBUTE.lastIndex = 0;
  let match;
  while ((match = ATTRIBUTE.exec(text)) !== null) {
    const name = match[1].toLowerCase();
    if (!attributes.has(name)) {
      const value = match[2] ?? match[3] ?? match[4] ?? '';
      attributes.set(name, decodeEntities(value).trim());
    }
  }

  return attributes;
}

/** An absolute http(s) address without its fragment, or null. */
function resolve(reference, base) {
  if (typeof reference !== 'string') {
    return null;
  }

  const trimmed = reference.trim();
  if (trimmed === '' || trimmed.startsWith('data:') || trimmed.startsWith('#')) {
    return null;
  }

  try {
    const url = new URL(trimmed, base);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      return null;
    }

    url.hash = '';
    return url.href;
  } catch {
    return null;
  }
}

/** The addresses of the @import rules of a stylesheet. */
function findCssImports(css) {
  const imports = [];
  const source = String(css).replace(/\/\*[\s\S]*?\*\//g, '');
  const pattern = /@import\s+(?:url\(\s*(?:"([^"]*)"|'([^']*)'|([^)\s]*))\s*\)|"([^"]*)"|'([^']*)')/gi;
  let match;
  while ((match = pattern.exec(source)) !== null) {
    const reference = match[1] ?? match[2] ?? match[3] ?? match[4] ?? match[5];
    if (reference) {
      imports.push(reference);
    }
  }

  return imports;
}

/** The addresses of the static imports of a JavaScript module (bare names like "react" are left out). */
function findModuleImports(js) {
  const imports = [];
  const source = String(js);
  const pattern = /(?:^|[;\n\r}\s])(?:import|export)\s*(?:[\w$*{}\s,]+?\s*from\s*)?["']([^"'\n\r]+)["']/g;
  let match;
  while ((match = pattern.exec(source)) !== null) {
    const reference = match[1];
    if (/^(?:\.{1,2}\/|\/|https?:\/\/)/i.test(reference)) {
      imports.push(reference);
    }
  }

  return imports;
}

/**
 * The stylesheets and scripts of a page, in document order.
 * @returns {{url: string, kind: 'style'|'script'|'module'}[]}
 */
function findPageResources(html, pageUrl) {
  const source = String(html).replace(/<!--[\s\S]*?-->/g, '');
  let base = pageUrl;
  const baseTag = /<base\b([^>]*)>/i.exec(source);
  if (baseTag) {
    const href = resolve(parseAttributes(baseTag[1]).get('href'), pageUrl);
    if (href) {
      base = href;
    }
  }

  const found = [];
  const seen = new Set();
  const add = (reference, kind, relativeTo = base) => {
    const url = resolve(reference, relativeTo);
    if (url && !seen.has(url)) {
      seen.add(url);
      found.push({ url, kind });
    }
  };

  const tag = /<(link|script|style)\b([^>]*)>/gi;
  let match;
  while ((match = tag.exec(source)) !== null) {
    const name = match[1].toLowerCase();
    const attributes = parseAttributes(match[2]);
    if (name === 'script') {
      const scriptEnd = source.toLowerCase().indexOf('</script', tag.lastIndex);
      if (scriptEnd >= 0) {
        tag.lastIndex = scriptEnd; // what a script contains is not markup
      }

      if (!attributes.has('src') || attributes.has('nomodule')) {
        continue;
      }

      const type = (attributes.get('type') || '').toLowerCase().split(';')[0].trim();
      if (type === 'module') {
        add(attributes.get('src'), 'module');
      } else if (SCRIPT_TYPES.has(type)) {
        add(attributes.get('src'), 'script');
      }
    } else if (name === 'link') {
      const relations = (attributes.get('rel') || '').toLowerCase().split(/\s+/).filter(Boolean);
      const href = attributes.get('href');
      if (relations.includes('stylesheet') && !relations.includes('alternate') && !attributes.has('disabled')) {
        add(href, 'style');
      } else if (relations.includes('preload')) {
        const as = (attributes.get('as') || '').toLowerCase();
        if (as === 'style') {
          add(href, 'style');
        } else if (as === 'script') {
          add(href, 'script');
        }
      } else if (relations.includes('modulepreload')) {
        add(href, 'module');
      }
    } else {
      const styleEnd = source.toLowerCase().indexOf('</style', tag.lastIndex);
      const css = source.slice(tag.lastIndex, styleEnd >= 0 ? styleEnd : source.length);
      for (const reference of findCssImports(css)) {
        add(reference, 'style');
      }

      if (styleEnd >= 0) {
        tag.lastIndex = styleEnd;
      }
    }
  }

  return found;
}

module.exports = { findPageResources, findCssImports, findModuleImports, parseAttributes, resolve };
