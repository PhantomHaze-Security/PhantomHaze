# PhantomHaze Extension Language (`.phext`) Guide

PhantomHaze extensions are plain-text `.phext` files.  
You import them from **Settings -> Extensions -> Import extension**.

## File structure

`.phext` uses line commands plus heredoc blocks:

- One command per line.
- Strings with spaces must be in double quotes.
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
  if (!window.phantomHazeExtensions?.["sample.extension"]?.settings?.showBanner) {
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
  - Adds a button to the top bar.
- `app.startpage "<url>"`
  - Overrides Web View start page while extension is enabled.
- `app.searchengine <google|bing|yahoo|duckduckgo|startpage>`
  - Overrides search engine while extension is enabled.
- `app.log "<message>"`
  - Writes a startup log message.

### Web View / website modifications

- `web.script <id> "<urlPattern>" <document-start|document-end> <<TAG ... TAG`
- `web.css <id> "<urlPattern>" <document-start|document-end> <<TAG ... TAG`
- `web.block <id> "<urlPattern>"`
  - Blocks matching requests.

`urlPattern` uses `*` wildcards, for example:
- `"*"` (all pages)
- `"https://*.github.com/*"`
- `"*://*.example.org/*"`

### Extension UI

- `ui.title "<window title>"`
- `ui.size <width> <height>`
- `ui.html <<TAG ... TAG`

If `ui.html` exists, PhantomHaze shows the extension launcher button in the bottom-right corner.

---

## Placeholders

You can use placeholders in `app.button` messages, `web.script`, `web.css`, and `ui.html`:

- `{{extension.id}}`
- `{{extension.name}}`
- `{{extension.version}}`
- `{{setting.<key>}}` (for your declared settings)

---

## Runtime behavior notes

- **Global toggle:** Settings -> Extensions -> **Enable all PhantomHaze extensions**
- **Per-extension toggle:** check/uncheck each installed extension in the list.
- **Uninstall all:** Settings -> Extensions -> **Uninstall all extensions**
- `document-start` and `document-end` scripts are both applied during each completed navigation cycle, with `document-start` rules evaluated first.
