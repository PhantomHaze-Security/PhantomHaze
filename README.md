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
Includes timed disable options with a live countdown banner so you always know when protection will automatically resume.
In Web View the protection status sits in a compact card at the right of the top bar; in the Log view it spans the full width.
Protection covers everything PhantomHaze shows, not just the main window: menus, tooltips, drop-down lists, dialogs, file and colour pickers, print, download and permission prompts are all PhantomHaze's own protected windows or drawn inside the page, and taskbar thumbnails, Alt+Tab and Peek show the app icon instead of a live preview. Browser features that would open a window screen capture could see (picture-in-picture, Windows notifications, sharing, screen sharing from web pages) are turned off.

###  True Ephemeral Privacy
* **Zero Session Footprint:** By default, web history and cookies are stored strictly in memory and wiped automatically the moment you close the app.
* **Optional Saved Cookies:** Turn on **Settings -> General -> Save cookies across sessions** to stay signed in to websites. It is off by default; saved cookies are encrypted with Windows DPAPI for your Windows account, kept for the time you choose (1 day to forever, like saved history; cookies a site meant to end when the browser closes are kept at most 30 days), and deleted as soon as you turn the setting off.
* **Persistent Favorites:** Keeps your bookmarked favorite sites safely saved across sessions without storing tracking cookies.
* **Logs You Control:** Logs (including the addresses of pages you open) are kept in `%LOCALAPPDATA%\PhantomHaze\logs`, so the Master Log covers everything since installation. Cookie names and values are never logged, and **Settings -> About -> Delete all logs** removes them at any time.

###  Modern Browser Suite
* **Clean Top Bar:** The PhantomHaze logo, a **Log | Web** switcher and a Settings gear, with Back, Forward, a rounded address box (Refresh inside it), favourites and zoom controls in Web View.
* **Tabbed Navigation:** Chrome-style tabs with live site favicons and title updates.
* **Web View Protocols:** Choose how pages load in **Settings -> General -> Web View protocol**: **WebView2** (the default, a full Microsoft Edge engine) or **HazeFetch**, where PhantomHaze downloads each page itself (over HTTP/2, with the browser's own request headers and cookies) and the tab shows it and runs its JavaScript like any other page, so everyday sites and light web games work as usual. Sites with bot checks or sign-in prompts, very large files and anything HazeFetch cannot fetch are handed back to the browser automatically. Sites cannot install service workers while HazeFetch is on, so every page they open still comes through PhantomHaze. Nothing is sent to any third party.
* **Fullscreen:** Videos and web games can go fullscreen; the top bar and tabs hide until you press Esc.
* **In-Page Search (`Ctrl+F`):** Fast search panel with real-time match highlighting.
* **Live Status Logging:** Instant visual feedback whenever protection is active or toggled.
* **Smooth View Switching:** Web View transitions wait for the page surface to be ready, with an inline loading state to prevent partial renders.

###  Make It Yours
* **Custom Themes:** Pick any colour for the whole window with the colour picker, or build a two-colour gradient theme (left to right, top to bottom or diagonal), alongside the Light and Dark themes.
* **Text Colour:** Theme default, White, Black, any custom colour - or **Rainbow**, which changes the text to a random colour every half second.
* **Google Fonts:** Paste a link from [Google Fonts](https://fonts.google.com) to use that font throughout the app. It is downloaded once and cached.
* **Live Clock:** The menu bar shows the time from your computer's clock, seconds included.

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
* **Extension Pages:** Extensions can add their own page, shown as another segment of the **Log | Web** switcher.
* **UI Customization:** Extensions can restyle PhantomHaze's own controls and replace them with SVG graphics or shapes (the protection banner is off-limits, so an extension can never fake your protection status).

Extension language reference: [`.claude/skills/phantomscript/SKILL.md`](./.claude/skills/phantomscript/SKILL.md)

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
