# Trayify

A small Windows 11 tray utility that sends other apps' windows to the notification area
instead of closing or minimizing them.

Requires Windows 11 (x64). Trayify runs elevated (see [How interception works](#how-interception-works)).

## Features

- **Close to tray, per app.** The settings window lists open windows (icon, title, exe, PID).
  Turn on **Close to tray** for one and that app's title-bar **X** hides the window to the tray
  instead of closing it. Rules are keyed by exe name (e.g. `notepad.exe`), saved across
  restarts, and you can remove them under *Close-to-tray apps*.
- **Per-app global shortcut (optional).** Each close-to-tray rule can have a shortcut. Under
  *Close-to-tray apps*, click **Set shortcut** and press the combination you want: Esc cancels,
  Backspace clears, and the ✕ button removes it. The shortcut is saved with the rule and
  registered at startup. If Windows refuses it because another app (or another rule) already uses
  that combination, the rule shows a red warning. Pressing the shortcut:
  - restores the app's window if Trayify has it hidden in the tray (focused, prior position);
  - hides the app to the tray if its window is the active window;
  - brings the app to the front (un-minimizing it if needed) if it's open but minimized or behind
    other windows;
  - does nothing if the app isn't running (Trayify doesn't launch apps).

  The shortcut is registered system-wide with `RegisterHotKey`, so the app doesn't see it while it
  is assigned. For example, if Ctrl+G is assigned to an app, Ctrl+G no longer reaches any other app.
- **Right-click minimize to tray (global toggle).** Right-click the minimize button of *any*
  window to hide it in the tray.
- **Alt+F4 to tray** (optional) for apps that have a close-to-tray rule.
- **Restore from the tray.** Every hidden window gets its own tray icon (the app's icon, with the
  title as tooltip). Left-click it to restore, or right-click for a menu. Trayify's own icon also
  has a menu that lists every hidden window plus *Restore all*. A restored window comes back in
  the same position and state (normal or maximized), along with any owned dialogs, and gets focus.
- **Nothing gets lost.** Quitting Trayify restores every hidden window first. Hidden windows are
  also recorded in `%LOCALAPPDATA%\Trayify\hidden.json`, and a small guardian process
  (`Trayify.exe --guardian`) restores them right away if Trayify crashes or is killed. If the
  guardian is killed too, the next Trayify start restores them (`Trayify.exe --recover` does the
  same by hand).
- **Lives in the tray**, with an optional *Start with Windows* setting (an elevated logon task, so
  there's no UAC prompt). Left-click the Trayify icon to open the settings window. Closing that
  window only hides it.
- WinUI 3 / Windows App SDK UI with Mica, Fluent controls, and a custom title bar. It follows the
  system light/dark theme.

## Build

Requirements: the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). You don't need Visual
Studio. NuGet packages come from nuget.org (see `nuget.config`).

```powershell
# Debug/Release build (unpackaged, self-contained Windows App SDK)
dotnet build src\Trayify\Trayify.csproj -c Release

# Self-contained publish (no .NET or Windows App Runtime install needed on the target)
dotnet publish src\Trayify\Trayify.csproj -c Release -o publish
.\publish\Trayify.exe            # asks for elevation (requireAdministrator)
```

Command-line options: `--tray` starts without opening the window, `--show` opens the window,
`--startup` is used by the logon task, `--no-guardian` skips the watchdog, `--recover` restores
windows left hidden by a crashed run, and `--cmd <command>` sends a control command to the
running instance.

## How interception works

There's no DLL injection. Trayify installs global low-level hooks (`WH_MOUSE_LL`,
`WH_KEYBOARD_LL`) on a dedicated thread. The OS calls the hook before the input reaches the
target app, and returning non-zero swallows that input.

1. **Close button.** On a left button *down*, Trayify finds the top-level window under the cursor.
   If that window's exe has a rule, Trayify works out whether the point is on its Close button
   (see below). If it is, Trayify swallows the press and the matching release, then hides the
   window when the button is released. If you drag off the button before releasing, nothing
   happens, the same as a real button.
2. **Minimize button.** A right-click on any window's minimize button is handled the same way
   (when the toggle is on).
3. **Which button is under the cursor?**
   - Trayify sends `WM_NCHITTEST` (`SendMessageTimeout`, 100 ms) and checks for `HTCLOSE` or
     `HTMINBUTTON`. Standard Win32 windows, WinUI/UWP-style apps, and Chromium/Electron apps that
     use the Window Controls Overlay (`titleBarOverlay`) answer this correctly, so
     they use this fast, exact path. DPI-unaware windows get logical coordinates
     (`PhysicalToLogicalPointForPerMonitorDPI`).
   - Some apps draw their own buttons in the client area (e.g. Discord), so hit-testing returns
     `HTCLIENT`. For those, a background worker uses **UI Automation** while the mouse hovers in
     the title-bar band. It looks for a `Button` named *Close* / *Minimize* in the right half of
     the title band and caches its rectangle, so the click handler itself only does a rectangle
     test. (Low-level hooks must return fast, so cross-process UIA calls can't run inside the hook.)
4. **Per-app shortcuts** use `RegisterHotKey` on Trayify's hidden tray window rather than the
   low-level hook. Windows delivers `WM_HOTKEY` no matter which window is focused (elevated ones
   included), consumes the key so the focused app doesn't also act on it, and lets the receiving
   process take the foreground. Repeats are suppressed (`MOD_NOREPEAT`). While the recorder is
   capturing a new shortcut, all shortcuts are temporarily unregistered.
5. **Alt+F4.** When Alt+F4 is pressed and the foreground window belongs to an app with a rule,
   Trayify swallows the key and hides the window. It also sends a harmless unassigned key so the
   app doesn't treat the lone Alt release as "open the menu bar".
6. **Hiding** is `ShowWindow(SW_HIDE)` on the window and its visible owned windows, which also
   removes the taskbar button. Activation moves to the next window in Z-order, as Windows does
   when a window closes. **Restoring** is `SW_SHOW`, then the saved rectangle if the window
   moved, then a reliable foreground switch.

Trayify **runs elevated** (`requireAdministrator` in `app.manifest`). Without elevation, UIPI
would block hit-testing and hiding windows of apps that run elevated. *Start with Windows*
therefore registers a Task Scheduler logon task with *Run with highest privileges*
(default path `\Trayify\Trayify at sign-in`, configurable as `StartupTaskPath` in
`settings.json`). The task runs `Trayify.exe --startup`, which is a GUI exe, so no console window
appears.

## Control commands (diagnostics / tests)

The running instance listens on the named pipe `Trayify.Control.<sessionId>` (current user only).
Send a command with `Trayify.exe --cmd <command>` or `tools\TrayifyTest pipe <command>`:

`ping`, `show`, `quit`, `status`, `list` (hidden windows), `restore <hwnd>`, `restore-all`,
`hide <hwnd>`, `pending`, `rules`, `add-rule <exe>`, `remove-rule <exe>`,
`set rightclick|altf4|uia on|off`, `probe <x> <y>` (what a click there would do),
`probe-uia <x> <y>`, `window`, `startup on|off|status`, `set-startup-path <path>`,
`hotkeys` (registration status), `set-hotkey <exe> <combo|none>` (e.g. `set-hotkey notepad.exe Ctrl+Alt+N`),
`toggle-app <exe>` (the same action as pressing the app's shortcut).

`tools/TrayifyTest` is the end-to-end test driver. It clicks the real caption buttons with
`SendInput` and checks the results through the pipe. Close clicks use a fail-safe protocol: the
driver presses the button, asks Trayify whether it swallowed the press, and releases on the button
only if it did. Otherwise it drags off the button before releasing, so a target window is never
actually closed.

## Files

`%LOCALAPPDATA%\Trayify\`: `settings.json` (rules and toggles), `hidden.json` (currently hidden
windows, used for crash recovery), and `trayify.log`.

## Known limitations

- Windows 11 puts new tray icons in the overflow (^) flyout by default. Pin Trayify's icon in
  *Settings > Personalization > Taskbar > Other system tray icons* to keep it visible. Per-window
  icons are new icons each time, so they appear in the overflow.
- Interception covers the mouse on the caption buttons and Alt+F4. Closing through the taskbar
  thumbnail/jump-list "Close window", the window's system menu, `Ctrl+W`/`Ctrl+Q`, or the app
  quitting itself still closes the app.
- The UI Automation fallback for app-drawn buttons depends on the app exposing a *Close* /
  *Minimize* button by name (English and a few other languages). It needs a short hover before the
  click. Chromium-based apps may switch on their accessibility tree when queried; you can turn the
  fallback off in settings.
- Touch and pen taps aren't intercepted (only mouse input goes through `WH_MOUSE_LL`).
- If an app shows its hidden window again by itself (for example when you launch it a second
  time), Trayify notices within about 1.5 s and drops its tray icon.
- Windows can silently remove a low-level hook whose callback is too slow. Trayify keeps the
  callback short (hit tests time out after 100 ms) and re-installs its hooks every 30 s.
- Running elevated means drag-and-drop from non-elevated Explorer into Trayify's window doesn't
  work (Trayify doesn't use it).
