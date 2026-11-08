# PhantomHaze Extension Language (`.phext`) Guide

PhantomHaze extensions are plain-text `.phext` files.  
You import them from **Settings -> Extensions -> Import extension**.

## File structure

`.phext` uses line commands plus heredoc blocks:

- One command per line.
- Strings with spaces must be in double quotes. `""` is an empty argument.
- `\` starts an escape inside arguments: `\n` (new line), `\t` (tab), `\r`, `\"` and `\\` (a literal backslash).
  Any other `\x` produces just `x`, so write `\\` when you need a backslash (for example in a Windows path).
- Comments start with `#` or `//`.
- Multi-line blocks use `<<TAG` and close with a line that contains only `TAG`.

---

## Minimal example

```text
meta.name "Sample Extension"
meta.id "sample.extension"
meta.version "1.0.0"
meta.description "Adds a menu button and page banner."
meta.icon "shield"

setting.toggle showBanner "Show banner on web pages" true
setting.text bannerText "Banner text" "Protected by Sample Extension"
setting.number bannerPadding "Banner padding (px)" 8 0 40
setting.select bannerPosition "Banner position" "top" "top|bottom"

app.button quickHello "Say hello" "Hello from {{extension.name}}"
app.startpage "https://example.com"
app.searchengine duckduckgo
app.log "Loaded {{extension.name}} v{{extension.version}}"

web.css bannerStyle "*" document-end <<CSS
#ph-sample-banner {
  position: fixed;
  left: 12px;
  right: 12px;
  padding: {{setting.bannerPadding}}px;
  background: #20242a;
  color: #ffffff;
  border-radius: 8px;
  z-index: 2147483646;
  font-family: "Segoe UI", sans-serif;
}
CSS

web.script bannerScript "*" document-end <<JS
(() => {
  if (!phantomHazeExtension.settings.showBanner) {
    return;
  }
  const id = "ph-sample-banner";
  let node = document.getElementById(id);
  if (!node) {
    node = document.createElement("div");
    node.id = id;
    document.body.appendChild(node);
  }
  node.style.top = "{{setting.bannerPosition}}" === "top" ? "12px" : "auto";
  node.style.bottom = "{{setting.bannerPosition}}" === "bottom" ? "12px" : "auto";
  node.textContent = "{{setting.bannerText}}";
})();
JS

web.block trackers "*://*.doubleclick.net/*"

ui.title "Sample Extension UI"
ui.size 820 560
ui.html <<HTML
<h2>Sample Extension</h2>
<p>This UI opens from the bottom-right launcher button.</p>
HTML
```

---

## Command reference

### Metadata

- `meta.name "<name>"`
- `meta.id "<id>"` (letters, numbers, `.`, `_`, `-`)
  - Recommended. Re-importing a file with the same id updates the installed extension. Without `meta.id`, the id is
    derived from the file name, so renaming the file installs it as a separate extension.
- `meta.version "<version>"`
- `meta.description "<description>"`
- `meta.icon "<icon>"`
  - Built-ins: `shield`, `warning`, `info`, `error`, `question`, `security`, `app`
  - Or base64 image: `base64:<...>`
  - Or data URI: `data:image/png;base64,<...>`

### Extension settings (shown in Settings -> Extensions)

- `setting.toggle <key> "<label>" <true|false>`
- `setting.text <key> "<label>" "<default>"`
- `setting.number <key> "<label>" <default> <min> <max>`
- `setting.select <key> "<label>" "<default>" "<opt1|opt2|opt3>"`

### App modifications

- `app.button <id> "<label>" "<message>"`
  - Adds a button to the top bar. Buttons that do not fit next to the built-in controls are listed under the `»` button.
- `app.startpage "<url>"`
  - Overrides Web View start page while extension is enabled. The value must be an `http`/`https` address
    (a bare host such as `example.com` is treated as `https://example.com`); invalid values are ignored.
- `app.searchengine <google|bing|yahoo|duckduckgo|startpage>`
  - Overrides search engine while extension is enabled.
- `app.log "<message>"`
  - Writes a log message when the extension starts (at app startup, or when it is installed or enabled).

### Web View / website modifications

- `web.script <id> "<urlPattern>" <document-start|document-end> <<TAG ... TAG`
- `web.css <id> "<urlPattern>" <document-start|document-end> <<TAG ... TAG`
- `web.block <id> "<urlPattern>"`
  - Blocks matching requests: pages, their iframes, workers (including service and shared workers), favicons and
    anything an extension window loads.

`urlPattern` uses `*` wildcards (any run of characters, case-insensitive), for example:
- `"*"` (all pages)
- `"https://*.github.com/*"` (github.com and all of its subdomains, over https)
- `"*://*.example.org/*"` (example.org and its subdomains, any scheme)

Patterns written as `scheme://host/path` are matched part by part: a wildcard in the scheme or host never matches
characters in the rest of the URL, so `https://*.github.com/*` does not match `https://evil.example/?a.github.com/`.
`*.` at the start of the host also matches the domain itself. A port is only compared when the pattern includes one
(`http://localhost:8080/*`), and a pattern without a path (`https://example.com`) matches every page on that host.
Patterns without `://` (for example `"*tracker*"`) are matched against the whole URL. An empty pattern is an error;
use `"*"` for every page.
Hosts are compared the way the browser reports them: a trailing dot is ignored (`ad.example.com.` is
`ad.example.com`), and internationalised domain names can be written in Unicode (`"https://*.bücher.de/*"`).

### Script context

Each `web.script` runs inside its own function and receives a private `phantomHazeExtension` object:

```js
phantomHazeExtension.id        // "sample.extension"
phantomHazeExtension.name      // "Sample Extension"
phantomHazeExtension.version   // "1.0.0"
phantomHazeExtension.settings  // { showBanner: true, bannerText: "...", bannerPadding: 8, bannerPosition: "top" }
```

Setting values keep their declared types: toggles are booleans, numbers are numbers, text and select values are strings.
The object is not stored on the page's `window`, so the website's own scripts cannot read or change your extension's
settings. Scripts written for earlier versions that read `window.phantomHazeExtensions["<id>"]` keep working: inside an
extension script that expression refers to the same private context.

### Extension UI

- `ui.title "<window title>"`
- `ui.size <width> <height>`
- `ui.html <<TAG ... TAG`

If `ui.html` exists, PhantomHaze shows the extension launcher button in the bottom-right corner.

---

## Placeholders

You can use placeholders in `app.button` messages, `app.log` messages, `web.script`, `web.css`, and `ui.html`:

- `{{extension.id}}`
- `{{extension.name}}`
- `{{extension.version}}`
- `{{setting.<key>}}` (for your declared settings)

Inserted values are encoded for the place they are used in:

- `web.script`: values are escaped for JavaScript string literals, so put the placeholder inside quotes, e.g.
  `node.textContent = "{{setting.bannerText}}";`. Quotes or other special characters in a setting can no longer break
  (or inject code into) your script. To use a setting as a number or boolean, read `phantomHazeExtension.settings`.
- `ui.html`: values are HTML-encoded, so they are displayed as text rather than interpreted as markup.
- `web.css`, `app.button` and `app.log`: values are inserted as-is.

---

## Runtime behavior notes

- **Global toggle:** Settings -> Extensions -> **Enable all PhantomHaze extensions**
- **Per-extension toggle:** check/uncheck each installed extension in the list.
- **Uninstall all:** Settings -> Extensions -> **Uninstall all extensions**
- `document-start` and `document-end` scripts are both applied during each completed navigation cycle, with `document-start` rules evaluated first.
- A script only runs if the page that executes it still matches its `urlPattern`; if the tab navigates away while
  scripts are being applied, the remaining scripts are skipped for the old page.
- If a `web.script` cannot run at all (usually because of a JavaScript syntax error), PhantomHaze writes a log entry
  the first time it happens. Errors thrown while the script runs are reported in the page's developer console.
- `web.css` is applied as a constructable style sheet, so a site's Content-Security-Policy cannot block it. Style
  sheets that use `@import` are added as a `<style>` element instead (constructable style sheets do not support
  `@import`), which a strict Content-Security-Policy can block.
- Saving changes in Settings refreshes open extension windows with the new settings, and closes the windows of
  extensions that were disabled or uninstalled. Already-loaded web pages pick up script and style changes on their next load.
- An extension window always keeps showing the extension's UI: web links the user clicks in it (including links that
  would open a new window) open in a new PhantomHaze tab instead.
