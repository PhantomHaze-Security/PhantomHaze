<div align="center">

# PhantomHaze

### *Invisible Browsing for the Modern Desktop*

**The lightweight, .NET 10 Windows browser engineered to keep your screen activity strictly confidential during recordings, streams, and shares.**

[![Download Latest Release](https://img.shields.io/badge/Download-PhantomHaze.exe-success?style=for-the-badge&logo=windows)](https://github.com/benandkim57-crypto/PhantomHaze/releases)
[![Community Chat](https://img.shields.io/badge/WhatsApp-Join_Community-25D366?style=for-the-badge&logo=whatsapp)](https://chat.whatsapp.com/K0PJLcSlP2hAt2HTLXQjd8)
[![Linktree Profile](https://img.shields.io/badge/Linktree-PhantomHaze57-39E09B?style=for-the-badge&logo=linktree)](https://linktr.ee/PhantomHaze57)

---

</div>

##  Why PhantomHaze?

Ever needed to check sensitive documents, account details, or private notes while sharing your screen or recording a stream? **PhantomHaze** uses native Windows hardware affinity APIs to render your browser window invisible to screen capture software while remaining fully visible to you.
---

## Standout Features

###  Stealth Capture Protection
Leverages native Windows `WDA_EXCLUDEFROMCAPTURE` API to block screen recorders, screenshot utilities, and desktop-sharing apps from capturing your window.

###  True Ephemeral Privacy
* **Zero Session Footprint:** Web history and session cookies are stored strictly in memory and wiped automatically the moment you close the app.
* **Persistent Favorites:** Keeps your bookmarked favorite sites safely saved across sessions without storing tracking cookies.

###  Modern Browser Suite
* **Tabbed Navigation:** Clean tab interface with live site favicons and title updates.
* **In-Page Search (`Ctrl+F`):** Fast search panel with real-time match highlighting.
* **Live Status Logging:** Instant visual feedback whenever protection is active or toggled.

---

##  Quick Start Comparison

Choose the installation method that fits your workflow:

| Method | Target User | Setup Time | Prerequisites |
| :--- | :--- | :--- | :--- |
| **Direct Executable** *(Recommended)* | Everyone | ⚡ **< 10 Seconds** | Windows 10/11 |
| **Codespaces Build** | Developers / Testers | 🛠️ ~2 Minutes | GitHub Account & Terminal Knowledge |

---

## 🚀 Installation Guide

### Option 1: Direct Download (Zero Setup - Recommended)

1. Go to the [PhantomHaze Releases Page](https://github.com/benandkim57-crypto/PhantomHaze/releases).
2. Expand the **Assets** section under the latest release.
3. Download the executable file (e.g., `PhantomHaze-vX.X.X.exe`).
4. Double-click `PhantomHaze.exe` to launch immediately. Check the top toolbar indicator to verify that capture protection is active.

---

### Option 2: Compile Raw Source via GitHub Codespaces (Advanced)

1. Create a GitHub Codespace in this repository by clicking **Code** > **Codespaces** > **+**.
2. Once the Codespace environment boots up, open a terminal and execute:
   ```bash
   bash compiler.sh
