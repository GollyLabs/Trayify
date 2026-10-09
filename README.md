# Trayify: Minimize to Tray & Close to Tray for Windows 11 and macOS

**Send any app to the system tray or menu bar with a click or a keyboard shortcut. Free and open source.**

Trayify is a free **minimize to tray utility**. **Close to tray** instead of quitting an app, **hide a window to the notification area** with a right-click on its minimize button, or use a **keyboard shortcut (hotkey) to hide and show an app**. The app keeps running in the background, and one click on its tray icon brings it back where you left it.

| Platform | Status | Docs |
|---|---|---|
| **Windows 11** (x64) | Available: build from source (pre-built downloads coming to [Releases](https://github.com/GollyLabs/Trayify/releases)) | [windows/README.md](windows/README.md) |
| **macOS** (menu bar) | Coming soon | [macos/](macos/) |

## Windows 11

- **Close to tray, per app:** clicking the X hides the window to the system tray instead of closing the app.
- **Right-click minimize to tray:** right-click the minimize button of any window to hide it in the notification area.
- **Per-app hotkey:** one shortcut (for example Ctrl+G) hides the app, brings it back, or brings it to the front.
- **Restore from the tray:** each hidden window gets its own tray icon, and Trayify's menu has *Restore all*.
- **Nothing gets lost:** quitting Trayify, or even a crash, restores every hidden window.
- **Start with Windows**, a native Windows 11 look (WinUI 3, Mica), and support for apps running as administrator and Electron apps like Discord and Slack.
- No code injection, no network access, no telemetry.

An alternative to tools such as RBTray, Traymond and 4t Tray Minimizer. Full guide, FAQ and build steps: **[windows/README.md](windows/README.md)**.

## macOS

A native Swift menu bar version is in development in [`macos/`](macos/). Coming soon.

## Repository layout

```
windows/   Windows 11 app (C#, WinUI 3 / Windows App SDK) and its test driver
macos/     macOS app (Swift), coming soon
```
