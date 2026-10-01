# PhantomHaze

PhantomHaze is a .NET 10 WinForms app for Windows that keeps its own window
hidden from many screen capture workflows by using
`SetWindowDisplayAffinity(..., WDA_EXCLUDEFROMCAPTURE)`.

It includes:
- Always-on protection toggle with status feedback.
- Built-in logging for protection and browser events.
- Optional Web View mode with tabs, favicon/title tab headers, and quick tab
  creation/close controls.
- Address bar favorites control with persisted favourites (the only browser data
  saved across sessions).
- Session-only web history and cookie storage for compatibility (both are kept
  only for the current app session and cleared when the app exits).
- Find in page via Ctrl+F with live match highlighting and a closeable search
  panel.

## How to install
Ready to get it up and running? We can help, here's a guide in case you haven't used GitHub before:
1. Go to this link: [https://github.com/benandkim57-crypto/PhantomHaze/releases](https://github.com/benandkim57-crypto/PhantomHaze/releases)
2. Find the latest release, then click on "Assets"
3. Finally, click on the .exe file to download it. It should be named something like "PhantomHaze-v?-?-?" or if you're downloading a Pre-Release "PhantomHaze-v?-?-?-beta"
4. And that's it! You can now launch PhantomHaze by double-clicking on the executable. It's as simple as that!

Alternatively, you can use a GitHub Codespace or a Linux machine to compile the latest _RAW version of PhantomHaze. This is not recommended for people who do not know how to use a terminal or command line. Please note that the latest _RAW version may have critical issues as _RAW versions are the latest possible version of PhantomHaze currently available. If you would like to download PhantomHaze using this method, follow the instructions below:
1. Create a GitHub Codespace in this repository (or a fork of this repository) by clicking the green Code button, then Codespaces, then +.
2. Once your Codespace has finished booting, open a terminal and run `bash compiler.sh`
3. Once compiled, a `tmp` folder should appear in the Codespace's directory. Navigate to `tmp/publish/PhantomHaze.exe`
4. Right-click this file, then click "Download"
5. You should be able to execute this file from your Downloads folder. You can now close the Codespace and begin using PhantomHaze. Enjoy!
