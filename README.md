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

###  True Ephemeral Privacy
* **Zero Session Footprint:** Web history and session cookies are stored strictly in memory and wiped automatically the moment you close the app.
* **Persistent Favorites:** Keeps your bookmarked favorite sites safely saved across sessions without storing tracking cookies.

###  Modern Browser Suite
* **Tabbed Navigation:** Clean tab interface with live site favicons and title updates.
* **In-Page Search (`Ctrl+F`):** Fast search panel with real-time match highlighting.
* **Live Status Logging:** Instant visual feedback whenever protection is active or toggled.
* **Smooth View Switching:** Web View transitions wait for the page surface to be ready, with an inline loading state to prevent partial renders.

### 🧩 PhantomHaze Extensions (`.phext`)
* **Importable Extension Files:** Load `.phext` files from **Settings -> Extensions -> Import extension**.
* **Global + Per-Extension Controls:** Toggle all extensions at once or disable individual installed extensions.
* **Extension UI Launcher:** Extensions with `ui.html` automatically get a bottom-right launcher button with their icon.
* **Web View Modification Hooks:** Extensions can inject JS/CSS and block matching web requests.
* **App Surface Customization:** Extensions can add settings fields and top-bar buttons, plus override start page/search engine while enabled.

Extension language reference: [`PHEXT_GUIDE.md`](./PHEXT_GUIDE.md)

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
