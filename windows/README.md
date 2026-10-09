# Trayify: Minimize to Tray & Close to Tray for Windows 11

**Send any app to the system tray with a click or a keyboard shortcut. Free, open source, and made for Windows 11.**

Trayify is a free **minimize to tray utility for Windows 11**. Use it to **close to tray** instead of quitting an app, to **hide a window to the notification area** with a right-click on its minimize button, or to set a **keyboard shortcut to hide and show an app**. When you click the X on Notepad, Outlook, Discord or Slack, the window goes to the system tray and the app keeps running. Click its tray icon and it comes back where you left it.

Trayify is an alternative to tools such as RBTray, Traymond and 4t Tray Minimizer.

> This is the Windows 11 version. A macOS menu bar version is [coming soon](../macos/); see the [project overview](../README.md).

---

## Contents

- [What it does](#what-it-does)
- [Key features](#key-features)
- [Download / install](#download--install)
- [How to use](#how-to-use)
- [FAQ](#faq)
- [Known limitations](#known-limitations)
- [Privacy](#privacy)
- [Build from source](#build-from-source)

---

## What it does

Lots of apps quit when you click the X. Some just sit in your taskbar when you'd like them gone. Trayify lets you choose what happens:

- **Close to tray:** pick an app, and clicking its X hides the window to the system tray. The app keeps running.
- **Minimize to tray:** right-click the minimize button of *any* window to hide it to the tray.
- **Hide/show with a shortcut:** give an app a keyboard shortcut, such as Ctrl+Alt+N, that hides it or brings it back.

A hidden window disappears from the screen and the taskbar and gets its own icon in the notification area. Click that icon to get the window back.

---

## Key features

- **Close to tray, per app.** Choose which apps should go to the tray when you click their X. Trayify remembers your choices after a restart.
- **Alt+F4 to tray (optional).** Alt+F4 can also send those apps to the tray. This is on by default and you can turn it off.
- **Right-click minimize to tray.** Right-click the minimize button of any window to hide it to the tray. This is on by default and you can turn it off.
- **Per-app keyboard shortcuts (optional).** One key combination hides the app, restores it, or brings it to the front.
- **One tray icon per hidden window.** Each icon shows the app's own icon, and hovering over it shows the window title. Left-click to restore, or right-click for a menu.
- **Restore all.** Trayify's own tray icon lists every hidden window and has a *Restore all* option.
- **Windows come back as they were.** A restored window has the same position and size, normal or maximized, along with any dialog windows it had open, and it gets focus.
- **Nothing gets lost.** Quitting Trayify brings back every hidden window. If Trayify crashes, a small helper process restores them right away.
- **Start with Windows (optional).** Trayify starts quietly in the tray when you sign in, with no UAC prompt.
- **Works with apps running as administrator.**
- **Works with apps that draw their own title bars,** including Electron/Chromium apps such as Discord, Slack and VS Code.
- **Modern Windows 11 look.** WinUI 3 with Mica and Fluent controls. It follows your light or dark theme.
- **No code injection** into other apps, and **no network access**.

**Requirements:** Windows 11, 64-bit (x64). Trayify runs as administrator.

---

## Download / install

**Pre-built downloads will be published on the [Releases page](https://github.com/GollyLabs/Trayify/releases); until then, build it from source (takes a couple of minutes).**

See [Build from source](#build-from-source) below. You only need the free .NET SDK, Git and a few commands. Visual Studio is not required.

---

## How to use

### 1. Start Trayify

Run `Trayify.exe`. Windows asks for administrator permission (UAC). Click **Yes**. The [FAQ](#why-does-trayify-ask-for-administrator-uac-permission) explains why.

Trayify's icon appears in the system tray, at the bottom-right of the taskbar. If you can't see it, click the **^** arrow. To keep it visible, see [how to pin it](#my-tray-icon-is-hidden-under-the--arrow-how-do-i-pin-it).

### 2. Open the settings window

Left-click the Trayify tray icon. The settings window lists your open windows, with each app's icon, window title and program name (for example `notepad.exe`).

Closing the settings window doesn't quit Trayify. It just goes back to the tray.

### 3. Turn on "Close to tray" for an app

1. Find the app under **Open windows**, for example Notepad. Click **Refresh** if you just opened it.
2. Turn on **Close to tray** for it. It now appears under **Close-to-tray apps**.
3. Click that app's **X** button. The window disappears into the tray and the app keeps running.

Trayify remembers the rule by program name. Next time you open Notepad, its X also sends it to the tray.

### 4. Get the window back

- **Left-click** the app's tray icon to restore it.
- **Right-click** the app's tray icon for more options.
- Or right-click **Trayify's own icon** to see a list of all hidden windows, plus **Restore all**.
- The settings window's **In the tray** section also lists hidden windows with a **Restore** button for each.

### 5. (Optional) Minimize any window to the tray

**Right-click minimize sends to tray** is on by default (you can turn it off in settings). **Right-click the minimize button** (the **–**) of any window to hide it to the tray. This works on any app; you don't need a rule for it.

### 6. (Optional) Add a keyboard shortcut for an app

1. Under **Close-to-tray apps**, click the shortcut button on the app's rule (shortcuts are available for apps that have a close-to-tray rule).
2. Press the key combination you want, such as **Ctrl+G** or **Ctrl+Alt+N**.
   - **Esc** cancels, **Backspace** clears, and **✕** removes the shortcut.
   - The combination must include **Ctrl, Alt, Shift or Win**. The exception is **F1–F24**, which work on their own.

What happens when you press the shortcut:

| The app is… | Pressing the shortcut… |
|---|---|
| Hidden in the tray | Restores it in its previous position and focuses it |
| The active window | Hides it to the tray |
| Open but minimized or behind other windows | Brings it to the front |
| Not running | Does nothing (Trayify doesn't launch apps) |

If the combination is already used by another app or another rule, a **red warning** appears on that rule. Pick a different one.

### 7. (Optional) Start with Windows

Turn on **Start with Windows** in settings. Trayify then starts quietly in the tray each time you sign in, without a UAC prompt.

### 8. Quit Trayify

Right-click the Trayify tray icon and choose **Quit Trayify**. Every hidden window is restored first.

---

## FAQ

### Why does Trayify ask for administrator (UAC) permission?

Windows doesn't let a normal app control windows of apps running as administrator. Trayify runs as administrator so it can hide and restore *all* your windows, including elevated ones. You see the UAC prompt only when you start Trayify yourself. If you turn on **Start with Windows**, it uses a Task Scheduler sign-in task with highest privileges, so there's no prompt at sign-in.

### Where did my window go?

It's hidden in the system tray, which is also called the notification area. Look for the app's icon at the bottom-right of the taskbar, and check under the **^** arrow too. You can also right-click Trayify's icon to see every hidden window, or choose **Restore all**. Quitting Trayify also brings every hidden window back.

### My tray icon is hidden under the ^ arrow. How do I pin it?

Windows 11 puts new tray icons in the **^** overflow menu by default. To keep Trayify's icon visible:

1. Open **Settings > Personalization > Taskbar**.
2. Expand **Other system tray icons**.
3. Turn on **Trayify**.

The icons for individual hidden windows are new each time, so they appear in the **^** overflow.

### How do I remove a rule or a shortcut?

- **Rule:** open Trayify's settings, go to **Close-to-tray apps**, and click **Remove** next to the app.
- **Shortcut:** click the **✕** next to the shortcut. While recording a shortcut, **Backspace** also clears it.

### Does Trayify inject code into other apps?

**No.** Trayify doesn't inject anything into other programs. It uses standard Windows features: keyboard and mouse hooks, which see clicks and key presses before apps do, and system-wide hotkeys. It hides windows the normal Windows way, which also removes their taskbar buttons.

### What happens if Trayify crashes or I quit it?

Your windows are safe:

- **When you quit,** Trayify restores every hidden window first.
- **If Trayify crashes or is killed,** a small helper process restores the hidden windows right away.
- **If the helper was also stopped,** the next time Trayify starts it restores anything left hidden.

Trayify keeps the list of hidden windows in `%LOCALAPPDATA%\Trayify\hidden.json` so it can do this.

### Does it work with Electron apps like Discord or Slack?

Yes, in most cases. Many apps, including some Electron/Chromium apps, use title-bar buttons that Windows understands, and those work directly. Some apps, such as Discord, draw their own buttons. For those, Trayify uses the **Detect app-drawn title bar buttons** setting, which is on by default. With these apps:

- Hover over the X or minimize button for a moment before you click.
- It only works if the app reports a button named "Close" or "Minimize" to Windows.

Chromium-based apps may turn on their accessibility support while you hover over their title bar. If you don't want that, you can turn this option off at any time.

---

## Known limitations

- **Tray icons start in the ^ overflow.** This is how Windows 11 works. [Pin Trayify's icon](#my-tray-icon-is-hidden-under-the--arrow-how-do-i-pin-it) to keep it visible. Icons for individual hidden windows always appear in the overflow.
- **Only the X button and Alt+F4 are caught.** These still really close the app:
  - "Close window" from the taskbar thumbnail or jump list
  - The window's system menu (top-left corner or Alt+Space)
  - App shortcuts such as Ctrl+W or Ctrl+Q
  - The app quitting by itself
- **Mouse only.** Touch and pen taps on the title-bar buttons aren't caught.
- **Apps that show themselves again.** If an app brings back its own hidden window, for example when you launch it again, Trayify notices within about 1.5 seconds and removes its tray icon.
- **Shortcuts are reserved.** While a shortcut is assigned in Trayify, no other app receives that key combination.
- **Apps that draw their own title bars** need the hover-before-click step described in the [FAQ](#does-it-work-with-electron-apps-like-discord-or-slack).
- **No drag-and-drop into Trayify's window.** Trayify runs as administrator, so it doesn't use drag-and-drop.

---

## Privacy

- **No network code at all.** Trayify never connects to the internet.
- **No telemetry, no accounts, no tracking.**
- Everything stays on your PC in `%LOCALAPPDATA%\Trayify\`:
  - `settings.json`: your rules and settings
  - `hidden.json`: currently hidden windows, used for crash recovery
  - `trayify.log`: a local log file
- Trayify accepts control commands, such as `Trayify.exe --cmd quit`, through a local channel (a named pipe) that only your user account can use. It is not reachable over the network.

---

## Build from source

You need Windows 11 x64, Git and the **.NET 10 SDK**. Visual Studio is not required.

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
git clone https://github.com/GollyLabs/Trayify
cd Trayify\windows
dotnet publish src\Trayify\Trayify.csproj -c Release -o ..\publish
..\publish\Trayify.exe
```

The Windows project lives in the [`windows/`](.) folder of the repo; the commands above put the app in a `publish` folder at the repo root (it's git-ignored). The build is self-contained, so you don't need to install .NET or the Windows App Runtime separately to run it.

**Command-line options**

| Option | What it does |
|---|---|
| `--tray` | Start in the tray without opening the settings window |
| `--show` | Open the settings window |
| `--recover` | Restore windows left hidden by a crashed run |
| `--cmd <command>` | Send a command to the running Trayify (e.g. `quit`, `status`, `restore-all`) |

### How it works (technical)

Trayify doesn't inject DLLs. It installs global low-level mouse and keyboard hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`) and blocks the click or Alt+F4 before the target app receives it. To check whether a click lands on a Close or Minimize button, it sends `WM_NCHITTEST` to the window. For apps that draw their own buttons, an optional **UI Automation** fallback finds the button while the mouse hovers over the title bar. Per-app shortcuts use `RegisterHotKey`. Windows are hidden with `ShowWindow(SW_HIDE)` and restored with their saved position. Trayify runs elevated so it can control windows of elevated apps. For more detail, see the source in [`src/Trayify`](src/Trayify).
