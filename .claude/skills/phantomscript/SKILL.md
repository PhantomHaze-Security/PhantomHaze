---
name: phantomscript-guide
description: A guide for how to code extensions for PhantomHaze into a PhantomScript file (.phext)
---
# PhantomScript (for `.phext` files) Guide

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

## Example: a page, a menu and a new look

This extension adds its own page (shown like **Logs** and **Web View**), a search button and a **Tools** dropdown to
the menu bar, and restyles the menu bar - including an SVG gear in a pill-shaped Settings button.

```text
meta.name "Focus Tools"
meta.id "focus.tools"
meta.version "2.0.0"
meta.description "A dashboard page, a tools menu and a new look for the menu bar."

setting.text query "Search for" "privacy tools"

app.page dashboard "Dashboard" <<HTML
<h1>Focus dashboard</h1>
<p>Searching for: {{setting.query}}</p>
<p><a href="https://example.com">example.com</a> opens in a new tab.</p>
HTML

menu.button search "Search" url "https://duckduckgo.com/?q={{setting.query}}"
menu.dropdown tools "Tools"
menu.option tools "Open the dashboard" page dashboard
menu.option tools "Show the logs" view logs
menu.option tools "Go to Web View" view web
menu.option tools "Write to the log" log "Hello from {{extension.name}}"
menu.option tools "Say hello" message "Hello!"

ui.element menu.bar background "#1f2937"
ui.element menu.title text "Focus"
ui.element menu.title foreground "#f9fafb"
ui.element menu.clock foreground "#93c5fd"
ui.element menu.clock fontstyle bold
ui.element menu.settings tooltip "Open Settings"
ui.element menu.settings foreground white
ui.shape menu.settings pill "#2563eb" "#93c5fd"
ui.svg menu.settings <<SVG
<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
  <circle cx="12" cy="12" r="3"/>
  <path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1 7 17M17 7l2.1-2.1"/>
</svg>
SVG

ui.svg dashboard <<SVG
<svg viewBox="0 0 24 24" fill="currentColor">
  <rect x="3" y="3" width="8" height="8" rx="1"/><rect x="13" y="3" width="8" height="5" rx="1"/>
  <rect x="13" y="10" width="8" height="11" rx="1"/><rect x="3" y="13" width="8" height="8" rx="1"/>
</svg>
SVG
ui.element dashboard text "Dashboard"
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
  - Or an SVG data URI: `data:image/svg+xml;base64,<...>` or `data:image/svg+xml,<url-encoded svg>`
    (see [SVG support](#svg-support))

### Extension settings (shown in Settings -> Extensions)

- `setting.toggle <key> "<label>" <true|false>`
- `setting.text <key> "<label>" "<default>"`
- `setting.number <key> "<label>" <default> <min> <max>`
- `setting.select <key> "<label>" "<default>" "<opt1|opt2|opt3>"`

### App modifications

- `app.button <id> "<label>" "<message>"`
  - Adds a button to the top bar that shows a message. It is the same as
    `menu.button <id> "<label>" message "<message>"`.
- `app.startpage "<url>"`
  - Overrides Web View start page while extension is enabled. The value must be an `http`/`https` address
    (a bare host such as `example.com` is treated as `https://example.com`); invalid values are ignored.
- `app.searchengine <google|bing|yahoo|duckduckgo|startpage>`
  - Overrides search engine while extension is enabled.
- `app.log "<message>"`
  - Writes a log message when the extension starts (at app startup, or when it is installed or enabled).

### Menu bar

Extensions can add buttons and dropdown menus to the top menu bar. They appear between **Web View** and the clock,
in the order the file declares them; buttons that do not fit are listed under the `»` button.

- `menu.button <id> "<label>" <action> "<value>"`
  - A button that runs one [action](#actions) when it is clicked.
- `menu.dropdown <id> "<label>"`
  - A button that opens a list of options (shown with `▾` after the label).
- `menu.option <dropdownId> "<label>" <action> "<value>"`
  - Adds an option to a dropdown declared above it with `menu.dropdown`. Options appear in file order (at most 50).

The ids of `app.button`, `menu.button`, `menu.dropdown` and `app.page` share one namespace: each must be unique within
the file. `menu.button`, `menu.dropdown` and `app.page` ids cannot use the name of a built-in element (such as
`menu.logs`), so `ui.*` lines are never ambiguous.

### Actions

`menu.button` and `menu.option` take an action and a value:

| Action    | Value                       | What happens                                                                              |
| :-------- | :-------------------------- | :---------------------------------------------------------------------------------------- |
| `message` | the message                 | Shows a message box (like `app.button`).                                                  |
| `url`     | an `http`/`https` address   | Opens the address in a new tab and switches to Web View.                                   |
| `page`    | the id of an `app.page`     | Shows that page of the extension.                                                         |
| `window`  | `""` (ignored)              | Opens the extension's `ui.html` window (the file must have a `ui.html` block).            |
| `view`    | `logs` or `web`             | Switches the main view to the log panel or to Web View.                                    |
| `log`     | the message                 | Writes a line to the log panel.                                                           |

`page` and `window` are checked when the file is imported: a page id that does not exist, or `window` without
`ui.html`, is an error.

### Extension pages

- `app.page <id> "<label>" <<TAG ... TAG`
  - Gives the extension its own page in the main window. A button with the label appears in the menu bar next to
    **Logs** and **Web View**; clicking it shows the page in place of the log panel or the web pages, and the button
    is highlighted like the other views while the page is shown. A page keeps its state while another view is shown.
  - The block is HTML. A fragment is wrapped in a page that follows the app's theme, text colour and font; a complete
    document (containing `<html`) is used as written.
  - Like extension windows, a page only ever shows the extension's own content: web links the user clicks open in a
    new PhantomHaze tab, and `web.block` rules apply to everything the page loads.

### Web View / website modifications

- `web.script <id> "<urlPattern>" <document-start|document-end> <<TAG ... TAG`
- `web.css <id> "<urlPattern>" <document-start|document-end> <<TAG ... TAG`
- `web.block <id> "<urlPattern>"`
  - Blocks matching requests: pages, their iframes, workers (including service and shared workers), favicons, the
    app font download and anything an extension window or page loads.

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

### Customising PhantomHaze's own controls

Extensions can change how PhantomHaze's built-in controls look, replace them with SVG graphics or shapes, and do the
same to the buttons, dropdowns and page buttons they add themselves.

- `ui.element <element> <property> "<value>"`
  - Changes one property of a control. See the [properties](#properties) and [elements](#elements) below.
- `ui.svg <element> <<TAG ... TAG` or `ui.svg <element> "data:image/svg+xml;base64,<...>"`
  - Replaces the control's look with an SVG graphic. On buttons and labels the graphic replaces the text (the usual
    text is shown when the mouse hovers over it); set `text` as well to show the graphic before your own text. On the
    logo the graphic replaces the logo image. Graphics that use `currentColor` follow the control's text colour, so
    they match the theme.
- `ui.shape <element> <shape> "<fill colour>" ["<outline colour>"]`
  - Gives the control a different outline - the control is cut to the shape - filled with the colour, with an
    optional outline drawn along the shape. On the logo, the shape replaces the image (an SVG on the logo is drawn
    inside the shape).
  - Shapes: `rectangle`, `rounded`, `pill`, `circle` (sized to the shorter side), `ellipse`, `diamond`, `triangle`,
    `hexagon`, `star`.

To change your own controls, use the id of the `app.button`, `menu.button`, `menu.dropdown` or `app.page` as the
element - for example `ui.svg dashboard <<SVG` for the page button of `app.page dashboard`. Own controls support every
property except `visible`.

All customisations are undone as soon as the extension is disabled or uninstalled. When two extensions change the
same property of the same control, the one listed later in **Settings -> Extensions** wins.

#### Properties

| Property    | Value                                                                                      |
| :---------- | :----------------------------------------------------------------------------------------- |
| `text`      | The control's text. Placeholders are filled in (plain text).                                |
| `tooltip`   | Text shown when the mouse hovers over the control. Placeholders are filled in.              |
| `visible`   | `true` or `false`.                                                                          |
| `background`| A colour.                                                                                   |
| `foreground`| A colour (the text colour, and the colour of SVG graphics that use `currentColor`).         |
| `border`    | A colour for the button's border.                                                           |
| `font`      | The name of a font installed on Windows, such as `"Consolas"` (or the app font's name).     |
| `fontsize`  | A size in points from 6 to 48, such as `12` or `10.5`.                                      |
| `fontstyle` | `regular`, `bold`, `italic` or `bold-italic`.                                              |

Colours are written as `#RGB`, `#RRGGBB`, `rgb(r, g, b)` or a CSS colour name such as `steelblue`. Window controls
cannot be see-through, so colours are always opaque. A font that is not installed is ignored (and reported once in the
log) rather than replaced by an unrelated fallback font.

#### Elements

`✓` marks what each built-in element supports (`font` includes `fontsize` and `fontstyle`).

| Element            | What it is                      | text | tooltip | visible | background | foreground | border | font | ui.svg | ui.shape |
| :----------------- | :------------------------------ | :--: | :-----: | :-----: | :--------: | :--------: | :----: | :--: | :----: | :------: |
| `menu.bar`         | The top menu bar                |      |         |         | ✓          |            |        |      |        |          |
| `menu.logo`        | The PhantomHaze logo            |      | ✓       | ✓       | ✓          |            |        |      | ✓      | ✓        |
| `menu.title`       | The PhantomHaze name            | ✓    | ✓       | ✓       | ✓          | ✓          |        | ✓    | ✓      | ✓        |
| `menu.logs`        | The Logs button                 | ✓    | ✓       | ✓       | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `menu.webview`     | The Web View button             | ✓    | ✓       | ✓       | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `menu.clock`       | The clock left of Settings      |      | ✓       | ✓       | ✓          | ✓          |        | ✓    |        | ✓        |
| `menu.settings`    | The Settings button             | ✓    | ✓       |         | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `address.bar`      | The address box                 |      | ✓       |         | ✓          | ✓          |        | ✓    |        |          |
| `address.favorite` | The favourite (star) button     |      | ✓       | ✓       |            |            | ✓      | ✓    | ✓      | ✓        |
| `address.back`     | The Back button                 | ✓    | ✓       | ✓       | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `address.go`       | The Go button                   | ✓    | ✓       | ✓       | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `address.refresh`  | The Refresh button              | ✓    | ✓       | ✓       | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `address.history`  | The History button              | ✓    | ✓       | ✓       | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `address.zoom`     | The zoom percentage             |      | ✓       | ✓       | ✓          | ✓          |        | ✓    |        |          |
| `tabs.new`         | The new tab (+) button          | ✓    | ✓       | ✓       | ✓          | ✓          | ✓      | ✓    | ✓      | ✓        |
| `log.panel`        | The log panel                   |      |         |         | ✓          | ✓          |        | ✓    |        |          |
| `history.panel`    | The history and favourites panel|      |         |         | ✓          |            |        |      |        |          |

Using a property an element does not support is an error when the file is imported, so mistakes are caught early.

Two parts of PhantomHaze can never be changed by an extension:

- **The protection banner and the protection button.** They are not elements at all, so an extension can never make
  PhantomHaze look protected from screen capture when it is not.
- **The Settings button cannot be hidden** (it can be restyled), because Settings is where extensions are disabled and
  uninstalled.

#### SVG support

PhantomHaze draws SVG graphics itself, so they work offline and cannot load anything. Supported:

- `<svg>` with `viewBox` (or `width`/`height`; without either, the drawing is fitted to its content). The graphic is
  scaled to fit the control and centred.
- Shapes: `<path>` (every path command, including arcs), `<rect>` (with `rx`/`ry`), `<circle>`, `<ellipse>`,
  `<line>`, `<polyline>`, `<polygon>`, grouped with `<g>`.
- Styling with attributes or `style="..."`: `fill`, `stroke`, `stroke-width`, `stroke-linecap`, `stroke-linejoin`,
  `stroke-miterlimit`, `fill-rule`, `opacity`, `fill-opacity`, `stroke-opacity`, `color`, `display`, `visibility`.
- `transform` (`matrix`, `translate`, `scale`, `rotate`, `skewX`, `skewY`).
- `currentColor`, which follows the control's text colour (or the SVG's own `color`).
- Gradients (`url(#id)`), drawn with the average of their colours.

Text, images, `<use>`, CSS `<style>` blocks, filters, masks and scripts are ignored. Graphics up to 1 MB are accepted.
Most icon sets (for example Feather, Lucide, Heroicons, Material Symbols) work as they are.

---

## Placeholders

You can use placeholders in `app.button` messages, `app.log` messages, `web.script`, `web.css`, `ui.html`, `app.page`,
the values of `menu.button` / `menu.option` actions, and `ui.element` `text` and `tooltip` values:

- `{{extension.id}}`
- `{{extension.name}}`
- `{{extension.version}}`
- `{{setting.<key>}}` (for your declared settings)

Inserted values are encoded for the place they are used in:

- `web.script`: values are escaped for JavaScript string literals, so put the placeholder inside quotes, e.g.
  `node.textContent = "{{setting.bannerText}}";`. Quotes or other special characters in a setting can no longer break
  (or inject code into) your script. To use a setting as a number or boolean, read `phantomHazeExtension.settings`.
- `ui.html` and `app.page`: values are HTML-encoded, so they are displayed as text rather than interpreted as markup.
- `url` actions: values are percent-encoded, so a setting can be used in a query string
  (`https://duckduckgo.com/?q={{setting.query}}`) but can never change which site is opened.
- `web.css`, `app.button`, `app.log`, `message` and `log` actions, and `ui.element` `text`/`tooltip`: values are
  inserted as-is.

---

## Runtime behavior notes

- **Global toggle:** Settings -> Extensions -> **Enable all PhantomHaze extensions**
- **Per-extension toggle:** check/uncheck each installed extension in the list.
- **Uninstall one extension:** select it in Settings -> Extensions and click **Uninstall this extension** at the bottom
  of its panel (this also works for extensions that failed to load). It is removed when you click **Save**.
- **Uninstall all:** Settings -> Extensions -> **Uninstall all extensions**
- **Backup and sync:** Settings -> Backup exports your settings and installed extensions (with their settings) to a
  `.phstg` file that can be imported into any PhantomHaze. When both the client and the file have extensions, you
  choose to **Skip** the file's extensions, **Merge** them with yours (an extension with the same id is updated to the
  file's version) or **Overwrite** yours with them. Extensions in a `.phstg` file are checked exactly like imported
  `.phext` files; broken ones are skipped.
- `document-start` and `document-end` scripts are both applied during each completed navigation cycle, with `document-start` rules evaluated first.
- A script only runs if the page that executes it still matches its `urlPattern`; if the tab navigates away while
  scripts are being applied, the remaining scripts are skipped for the old page.
- If a `web.script` cannot run at all (usually because of a JavaScript syntax error), PhantomHaze writes a log entry
  the first time it happens. Errors thrown while the script runs are reported in the page's developer console.
- `web.css` is applied as a constructable style sheet, so a site's Content-Security-Policy cannot block it. Style
  sheets that use `@import` are added as a `<style>` element instead (constructable style sheets do not support
  `@import`), which a strict Content-Security-Policy can block.
- Saving changes in Settings refreshes open extension windows and pages with the new settings, and closes the windows
  and pages of extensions that were disabled or uninstalled (if a closed page was shown, the log panel is shown
  instead). Already-loaded web pages pick up script and style changes on their next load.
- An extension window or page always keeps showing the extension's own content: web links the user clicks in it
  (including links that would open a new window) open in a new PhantomHaze tab instead.
- Extension windows and pages follow the app's theme (a gradient theme becomes a CSS gradient background), the text
  colour (Rainbow uses the theme's colour) and the app font.
