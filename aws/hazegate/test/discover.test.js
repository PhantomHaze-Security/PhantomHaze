'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { findPageResources, findCssImports, findModuleImports } = require('../function/src/lib/discover');

test('stylesheets, scripts, preloads and module scripts are found in document order', () => {
  const html = `<!doctype html><html><head>
    <base href="https://cdn.example/assets/">
    <link rel="stylesheet" href="site.css?v=1&amp;x=2">
    <link rel="alternate stylesheet" href="dark.css">
    <link rel="preload" as="style" href="/fonts.css">
    <link rel="preload" as="image" href="hero.jpg">
    <link rel="modulepreload" href="chunk.js">
    <!-- <script src="commented.js"></script> -->
    <script src="app.js"></script>
    <script type="module" src="main.mjs"></script>
    <script nomodule src="legacy.js"></script>
    <script type="application/ld+json">{"a": "<script src='inside.js'></script>"}</script>
    <script>var s = '<link rel="stylesheet" href="inside.css">';</script>
    <style>@import url("print.css"); body { color: red }</style>
    <link rel="stylesheet" href="site.css?v=1&x=2#again">
  </head><body></body></html>`;
  const found = findPageResources(html, 'https://www.example/page');
  assert.deepEqual(found, [
    { url: 'https://cdn.example/assets/site.css?v=1&x=2', kind: 'style' },
    { url: 'https://cdn.example/fonts.css', kind: 'style' },
    { url: 'https://cdn.example/assets/chunk.js', kind: 'module' },
    { url: 'https://cdn.example/assets/app.js', kind: 'script' },
    { url: 'https://cdn.example/assets/main.mjs', kind: 'module' },
    { url: 'https://cdn.example/assets/print.css', kind: 'style' },
  ]);
});

test('addresses that are not http(s) are ignored', () => {
  const html = '<script src="javascript:alert(1)"></script><link rel=stylesheet href="data:text/css,a{}"><script src=//other.example/x.js></script>';
  assert.deepEqual(findPageResources(html, 'https://www.example/'), [{ url: 'https://other.example/x.js', kind: 'script' }]);
});

test('@import rules are found in stylesheets', () => {
  const css = '/* @import "ignored.css"; */ @import url(a.css); @import url( \'b.css\' ) screen; @import "c.css"; a { b: c }';
  assert.deepEqual(findCssImports(css), ['a.css', 'b.css', 'c.css']);
});

test('static module imports are found, bare names are left out', () => {
  const js = `import a from "./a.js";\nimport { b, c } from '../b.js';\nimport "/side-effect.js";\nexport * from "https://cdn.example/x.js";\nimport React from "react";\nconst later = import("./lazy.js");`;
  assert.deepEqual(findModuleImports(js), ['./a.js', '../b.js', '/side-effect.js', 'https://cdn.example/x.js']);
});
