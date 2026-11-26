<div align="center">

# PhantomHaze

### *Invisible Browsing for the Modern Desktop*

**The lightweight, .NET 10 Windows browser engineered to keep your screen activity strictly confidential during recordings, streams, and shares.**

[![Download Latest Release](https://img.shields.io/badge/Download-PhantomHaze.exe-success?style=for-the-badge&logo=windows)](https://github.com/benandkim57-crypto/PhantomHaze/releases)
[![Community Chat](https://img.shields.io/badge/WhatsApp-Join_Community-25D366?style=for-the-badge&logo=whatsapp)](https://chat.whatsapp.com/K0PJLcSlP2hAt2HTLXQjd8)
[![Linktree Profile](https://img.shields.io/badge/Linktree-PhantomHaze57-39E09B?style=for-the-badge&logo=linktree)](https://linktr.ee/PhantomHaze57)
[![Website Redirect](https://img.shields.io/badge/website-redirect-grey?style=for-the-badge&color=blue)](https://phantomhaze-security.github.io/phantomhaze-web/)


---

</div>

##  Why PhantomHaze?

#### Ever needed to check sensitive documents, account details, or private notes while sharing your screen or recording a stream? **PhantomHaze** uses native Windows hardware affinity APIs to render your browser window invisible to screen capture software while remaining fully visible to you.
---

## Standout Features

###  Stealth Capture Protection
Leverages native Windows `WDA_EXCLUDEFROMCAPTURE` API to block screen recorders, screenshot utilities, and desktop-sharing apps from capturing your window.
The protection banner shows the real protection state at a glance: green while you are protected, red while you are not. In Web View it is a card beside the address bar with an **ON**/**OFF** switch; in the Logs view it runs across the window with the **Disable Protection** button below it. Turn protection off for a while (from 5 minutes to 1 day, or until you turn it back on) and a live countdown shows when it will automatically resume. With the **Simple** top bar it becomes a small **ON**/**OFF** switch next to Settings, and with the **Compact** top bar a green or red dot on the drop-down button (see [Top bar styles](#-top-bar-styles)).

###  True Ephemeral Privacy
* **Zero Session Footprint:** Web history and session cookies are stored strictly in memory and wiped automatically the moment you close the app.
* **Optional Saved Cookies:** Want to stay signed in? Turn on **Settings -> General -> Save cookies across sessions** (off by default, like saved history). Cookies are kept encrypted for your Windows account and deleted after the time you choose (1 day to forever); **Clear cookies** removes them at any time.
* **Persistent Favorites:** Keeps your bookmarked favorite sites safely saved across sessions without storing tracking cookies.
* **Logs You Control:** Logs (including the addresses of pages you open) are kept in `%LOCALAPPDATA%\PhantomHaze\logs`, so the Master Log covers everything since installation. Cookie names and values are never logged, and **Settings -> About -> Delete all logs** removes them at any time.

###  Modern Browser Suite
* **Switch users:** Click the person beside Settings to select a profile. PIN-protected profiles prompt for their PIN.
* **Manage favourites:** Select a favourite in History and press Delete or Backspace to remove it.
* **Tabbed Navigation:** Clean tab interface with live site favicons and title updates, plus Back (`←`), Forward (`→`), Go (`⏎`), Refresh (`⟳`) and History (`🕘`) buttons.
* **Keyboard Shortcuts:** `Alt+←` / `Alt+→` (back and forward), `Ctrl+L`, `Alt+D` or `F6` (address box), `Ctrl+T` (new tab, or double-click next to the tabs), `Ctrl+W` / `Ctrl+F4` (close the tab), `F5` / `Ctrl+R` (reload), `Ctrl+H` (history and favourites) and `Ctrl+F` (find in page).
* **Background Tabs & Resource Saver:** Tabs you are not looking at keep running for the time you choose in **Settings -> General -> Render background tabs for** (1 minute by default, from 10 seconds to forever), then pause until you open them again; a paused tab shows 💤 instead of its icon. With more than 20 tabs open, the resource saver pauses them after 10 seconds instead, so PhantomHaze stays responsive (it can be turned off from its banner or in Settings).
* **In-Page Search (`Ctrl+F`):** Fast search panel with real-time match highlighting.
* **Web View Protocols:** Choose how Web View loads pages in **Settings -> General -> Web View protocol** (see [Web View protocols](#-web-view-protocols)).
* **Live Status Logging:** Instant visual feedback whenever protection is active or toggled.
* **Smooth View Switching:** Web View transitions wait for the page surface to be ready, with an inline loading state to prevent partial renders.

###  Make It Yours
* **Custom Themes:** Pick any colour for the whole window with the colour picker, or build a two-colour gradient theme (left to right, top to bottom or diagonal), alongside the Light and Dark themes.
* **Text Colour:** Theme default, White, Black, any custom colour - or **Rainbow**, which changes the text to a random colour every half second.
* **Google Fonts:** Paste a link from [Google Fonts](https://fonts.google.com) to use that font throughout the app. It is downloaded once and cached.
* **Live Clock:** The menu bar shows the time from your computer's clock, seconds included.
* **Top Bar Styles:** Comfortable, Comfy, Compact or Simple in **Settings -> Appearance -> Top bar** (see [Top bar styles](#-top-bar-styles)).

###  Backup, Sync & Diagnostics
* **Export / Import (`.phstg`):** Settings -> Backup saves your settings and installed extensions (with their settings) to a `.phstg` file. Import it into any PhantomHaze to sync it straight away; if both have extensions, choose to **Skip**, **Merge** or **Overwrite**.
* **About & Master Log:** Settings -> About shows the version, app size, how much data PhantomHaze stores and technical details, and downloads every log since installation as one `.zip`.
* **Quieter Logs:** New cookies are summarised in a single line (`12 cookies were registered`) instead of one line per cookie.

### 🧩 PhantomHaze Extensions (`.phext`)
* **Importable Extension Files:** Load `.phext` files from **Settings -> Extensions -> Import extension**.
* **Global + Per-Extension Controls:** Toggle all extensions at once, disable individual installed extensions, or uninstall a single extension from its settings panel.
* **Extension UI Launcher:** Extensions with `ui.html` automatically get a bottom-right launcher button with their icon.
* **Web View Modification Hooks:** Extensions can inject JS/CSS and block matching web requests.
* **App Surface Customization:** Extensions can add settings fields, menu bar buttons and dropdown menus, plus override start page/search engine while enabled.
* **Extension Pages:** Extensions can add their own page, shown in the main window just like Logs and Web View.
* **Extension Protocols:** Extensions can add Web View protocols, for example to load pages through a server or a proxy.
* **UI Customization:** Extensions can restyle PhantomHaze's own controls and replace them with SVG graphics or shapes (the protection banner is off-limits, so an extension can never fake your protection status).

Extension authors can find the command reference and examples in the [PhantomScript wiki](./phantomscript-wiki.md).

---

## 🌐 Web View Protocols

A **protocol** is the way Web View loads and shows web pages. Pick one in **Settings -> General -> Web View protocol**;
open tabs are reopened with the new protocol when you click **Save**.

| Protocol | How it works | Good for |
| :--- | :--- | :--- |
| **WebView2** *(default)* | Microsoft Edge WebView2 (Chromium). | Everything: modern sites, JavaScript, extension scripts and styles, zoom, find in page and saved cookies. |
| **HTMLRenderer** | Downloads each page with the `curl.exe` built into Windows and draws it with [HTML Renderer](https://github.com/ArthurHub/HTML-Renderer), without WebView2. Pages are parsed like a browser parses them ([AngleSharp](https://anglesharp.github.io/)) and run their JavaScript with [Jint](https://github.com/sebastienros/jint), a JavaScript engine written in C#; PhantomHaze works out modern CSS itself (variables, media queries, `calc()`, modern colours, flex and grid layouts) and turns it into what HTML Renderer can draw. Form fields are real text boxes, lists and checkboxes. | A lighter, WebView2-free browser. jQuery, React and Vue pages work; very complex web apps may look simpler or be slow. Scripts can be turned off in **Settings -> General**. Every download is checked against extensions' `web.block` rules. |
| **HazeGate** | Like HTMLRenderer, but nothing is downloaded by your PC: each address goes to the HazeGate Azure function, which downloads the page with its stylesheets and scripts and sends everything back. Images and the page's own requests go through it too. | Hiding your IP address and network from the sites you visit. Nothing to set up (see [HazeGate](#hazegate-cloud-web-view-protocol)). |
| **Legacy** | The Internet Explorer engine built into Windows (the legacy WebBrowser control). WebView2 is not used. | Old intranet sites and pages made for Internet Explorer. Its dialogs, menus and pop-up windows are suppressed or opened as protected tabs, and its cookies only last until PhantomHaze closes. |
| **Extension protocols** | Added by extensions with `protocol.add`: one of the engines above, with an optional gateway address and proxy server. | Loading pages through a server or proxy. They appear as *Name (from Extension)*. |

If the chosen protocol cannot be used (for example its extension was turned off), Web View uses WebView2 and says so
in the log. Whatever the protocol, the PhantomHaze window stays protected from screen capture.

HTMLRenderer and HazeGate display inline `srcdoc` and same-origin HTML iframe previews, including their styles and images. Previews use a separate document with scripts off. External embeds have a link to open the page in a tab.

Developers: see [Adding a Web View protocol](#-adding-a-web-view-protocol-developers) to build another one.

### HazeGate (cloud Web View protocol)

HazeGate pages are downloaded by an [Azure Function](https://learn.microsoft.com/azure/azure-functions/) from the
[azure-cloud-webview-protocol](https://github.com/PhantomHaze-Security/azure-cloud-webview-protocol) repository and
drawn by PhantomHaze's HTMLRenderer engine, so the sites you visit only ever see Azure. Just choose **HazeGate** in
**Settings -> General -> Web View protocol**.

In this beta, the function's address and key are built into PhantomHaze (`HazeGateConnection` in
[`HazeGateProtocol.cs`](./HazeGateProtocol.cs)); they are not shown or changeable in Settings. To point a build at
another function, or after renewing the key in Azure Portal (**Functions -> hazegate -> Function Keys**), change them
there and rebuild. HazeGate keeps cookies on your PC (in memory, like the rest of a session) and only sends a site the
cookies that belong to it; the function itself keeps nothing.

---

## 🧭 Top Bar Styles

Choose how the top bar looks in **Settings -> Appearance -> Top bar**:

| Style | What it looks like |
| :--- | :--- |
| **Comfortable** *(default)* | The full top bar. |
| **Comfy** | Every control, closer together and a little smaller, to leave more room for pages. |
| **Compact** | Only **Logs**, **Web** and extension buttons. The **▾** button at the right shows the rest - the protection banner or strip, the Web View address and tab rows, the clock and Settings - and its dot is green while protection is on and red while it is off. |
| **Simple** | A small **ON**/**OFF** protection switch next to Settings (click it to choose how long to turn protection off), and in Web View only Back, Forward, the address box and small tabs, together in one row. Use `Ctrl+T` (or double-click next to the tabs) for a new tab, `F5` to reload and `Ctrl+H` for history. |

Extensions that hide built-in controls with `ui.element` keep working with every style: a control is shown only when
neither the style nor an extension hides it.

---

##  Quick Start Comparison

Choose the installation method that fits your workflow:

| Method | Target User | Setup Time | Prerequisites |
| :--- | :--- | :--- | :--- |
| **Direct Download** *(Recommended)* | Everyone | ⚡ **< 10 Seconds** | Windows 10/11 |
| **Installer Download** *✨NEW✨* | Everyone | ✨ **1 minute** | Windows 10/11 |
| **_RAW Download** | Developers / Testers | 🛠️ ~2 Minutes | GitHub Account & Terminal Knowledge |

---

## 🚀 Installation Guide

### Option 1: Direct Download (Zero Setup - Recommended)

1. Go to the [PhantomHaze Releases Page](https://github.com/benandkim57-crypto/PhantomHaze/releases).
2. Expand the **Assets** section under the latest release.
3. Download the executable file (e.g., `PhantomHaze-vX.X.X.exe`).
4. Double-click the downloaded file to launch immediately. Enjoy your app!

---

### Option 2: Installer Download
*Please note that this method is relatively new and will only be available on newer stable releases. This method may not be compatible with some devices. This installation method is powered by IZArc*

1. Go to the [PhantomHaze Releases Page](https://github.com/benandkim57-crypto/PhantomHaze/releases).
2. Expand the **Assets** section under the latest release.
3. Download the executable file (e.g., `PhantomHaze-installer-vX.X.X.exe`).
4. Double-click the downloaded file.
5. Follow the installation steps, including selecting the directory for the installation. We strongly recommend you use `C:/Program Files/PhantomHaze Security/` as the installation directory.
6. When the installer is complete, you can safely close and delete the installer, the app will be available by searching for `PhantomHaze` in the search bar on your taskbar if you installed the app to `C:/Program Files/PhantomHaze Security/`. Enjoy your app!

---

### Option 3: _RAW Download (Advanced users only)

1. Create a GitHub Codespace in this repository by clicking **Code** > **Codespaces** > **+**. Or you can download the code onto your computer.
2. Once the Codespace environment or PC is ready, open a terminal and run one of the following:

   #### Bash (Linux only):
   ```bash
   bash compiler/compiler.sh
   ```
   OR
   ```bash
   bash ./compiler/compiler.sh
   ```

   #### Windows PowerShell (Windows only):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\compiler\compiler.ps1
   ```

   #### Windows Powershell (custom output directory):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\compiler\compiler.ps1 .\compiler\tmp\MyBuild\publish
   ```

3. Once the compiler has finished running, navigate to `compiler/tmp/PhantomHaze/publish/PhantomHaze.exe`
4. Right-click the file, then click "Download" in the dropdown menu
5. Navigate to your Downloads folder and find the .exe file.
6. Double-click the file to execute it. You're done!

---

## 🔌 Adding a Web View Protocol (Developers)

Every protocol is a pair of classes: a `WebProtocol` that describes it (one instance, listed in Settings) and a
`WebProtocolPage` that shows one tab's pages. Web View only talks to these two classes, so a new protocol needs no
changes anywhere else. The base classes, and the rules below, are in [`WebProtocols.cs`](./WebProtocols.cs).
Use the existing protocols as examples:

* [`WebView2Protocol.cs`](./WebView2Protocol.cs): wraps a browser control and translates its events.
* [`HtmlRendererProtocol.cs`](./HtmlRendererProtocol.cs) and [`HazeGateProtocol.cs`](./HazeGateProtocol.cs): download
  pages themselves (an `IRenderedPageTransport`) and leave opening, scripting and drawing them to
  [`RenderedProtocolPage.cs`](./RenderedProtocolPage.cs) and the page engine (`HtmlRendererEngine.cs`,
  `HtmlRendererCss.cs`, `HtmlRendererMarkup.cs`).
* [`LegacyProtocol.cs`](./LegacyProtocol.cs): an engine that runs inside PhantomHaze and can open windows of its own.

### 1. Describe the protocol (`WebProtocol`)

Create a new file, for example `MyProtocol.cs`, with a class that derives from `WebProtocol`:

| Member | What to return |
| :--- | :--- |
| `Id` | A short lowercase id, such as `"myprotocol"`. It is saved in `settings.json`, so **never change it** once released. Add it as a constant next to the others in `WebProtocolCatalog`. |
| `DisplayName` | The name shown in Settings and in the log. |
| `Description` | One or two sentences shown under the protocol list in Settings: how it loads pages and what does not work. |
| `Features` | What its pages support: `Zoom`, `FindInPage`, `ExtensionScripts` (runs `web.script`/`web.css` through `ExecuteScriptAsync`), `JavaScript`, `SavedCookies`. Web View turns its controls (zoom slider, `Ctrl+F`) on or off to match. |
| `GetUnavailableReason()` | Why it cannot run on this PC (for example a missing runtime), or `null`. Keep it quick: Settings calls it. |
| `CreatePage(IWebProtocolHost host)` | A new page for one tab. |
| `Signature` *(optional)* | Change it whenever pages would behave differently; open tabs are reopened when it changes. |
| `LoadsPagesDirectly` *(optional)* | `false` if pages go through another server; PhantomHaze then never contacts the sites itself (for example for tab icons). |

A protocol that depends on settings (like whether HTMLRenderer and HazeGate run scripts) gets them through
its constructor from `BuiltInProtocolSettings`, and includes them in its `Signature`.

### 2. Show the pages (`WebProtocolPage`)

Derive a class from `WebProtocolPage`. Create its control in the constructor (with `Dock = DockStyle.Fill` and
`Visible = false`) and return it from `View`. Then implement:

* `InitializeAsync()`: prepares the page; throw a `WebProtocolUnavailableException` when it cannot be used.
* `IsReady`, `CurrentUrl` (the address shown in the address bar), `Title`, `CanGoBack` and `CanGoForward`.
* `Navigate(url)` (always an `http://` or `https://` address), `GoBack()`, `GoForward()` and `Reload()`.
* Report what happens by calling `OnNavigationStarting`, `OnNavigationCompleted`, `OnSourceChanged`, `OnTitleChanged`,
  `OnHistoryChanged`, `OnNewWindowRequested` and `OnFindRequested` (`Ctrl+F` pressed inside the page).
* Optional, to match `Features`: `SetZoom`, `FindAsync`, `ExecuteScriptAsync`, `GetDeclaredFaviconUrlAsync` and
  `DocumentUrl`. A page that downloads its parts itself can also download its tab icon the same way
  (`DownloadsOwnFavicon` and `DownloadFaviconAsync`).
* Optional: if the engine can pause a page, return `true` from `CanPauseRendering`. `SetRenderingActive(false)` is then
  called when a tab has been in the background for the time chosen in Settings (or by the resource saver), and
  `SetRenderingActive(true)` when it should continue; paused tabs show 💤.
* `Dispose(bool disposing)`: stop downloads and release the control.

### 3. Register it

Add an instance to `WebProtocolCatalog.CreateBuiltIn` in [`WebProtocols.cs`](./WebProtocols.cs). It then appears in
**Settings -> General -> Web View protocol** in that position.

To let extensions use it as an engine for `protocol.add`, also add a value to `PhantomExtensionProtocolEngine` and its
name to `PhextParser.ParseProtocolEngine` (both in [`PhantomExtensionRuntime.cs`](./PhantomExtensionRuntime.cs)), a case
to `WebProtocolCatalog.CreateEngine`, and document the name in the
[PhantomScript guide](./.claude/skills/phantomscript/SKILL.md).

### Rules every protocol must follow

These keep PhantomHaze protected from screen capture and private, whatever the protocol:

* **Never open a window of your own.** Only PhantomHaze's windows are excluded from capture. Report new windows with
  `OnNewWindowRequested` (PhantomHaze opens them as protected tabs) and turn off the engine's own dialogs, context menus
  and download prompts. If the engine runs inside PhantomHaze and could still open a window, call
  `Host.SetUiThreadWindowGuard(this, true)` in the constructor and `Host.SetUiThreadWindowGuard(this, false)` in
  `Dispose`, and every window it opens is protected as it appears.
* **Never start other programs** or let the engine open files or links with Windows.
* **Only load `http://` and `https://` addresses,** and check every request you make - pages, frames, redirects, images,
  stylesheets - with `Host.IsBlocked(url, out string blockedBy)`, so extensions' `web.block` rules apply. Requests made
  on other threads use a snapshot from `Host.GetRequestRules()` (take a new one for every page load).
* **Keep temporary files in `Host.SessionDirectory`.** It is private to this run and deleted when PhantomHaze closes.
* **Work on the UI thread.** Pages are created, used and disposed there, and must raise their events there; do slow work
  in background tasks.
* **Log with `Host.Log`,** and never log cookie values or page contents.
* Apply extensions' `web.css` with `Host.GetExtensionStyleSheets(url)` if the engine can show it.

### Test it

* Open pages, go Back and Forward, Refresh, open and close tabs, and click links that open a new window (`target="_blank"`).
* Try history, favourites and, if supported, zoom and `Ctrl+F`.
* Switch to and from the protocol in Settings while tabs are open.
* Add an extension with a `web.block` rule and check that matching pages and images are blocked.
* Start a screen recording or screen share and check that nothing the protocol shows (pages, pop-ups, dialogs,
  menus) appears in it.
