# Trayify for macOS

**Send apps to the menu bar instead of closing or minimizing them.**

This is the native macOS version of [Trayify](../README.md). It's a small menu bar app (Swift 6, SwiftUI + AppKit,
macOS 14 Sonoma or later, Apple silicon and Intel). It has no Dock icon, no network access, no telemetry, and it
doesn't inject code into, patch or re-sign other apps. It uses only public macOS APIs (Accessibility, a CGEvent tap,
Carbon hot keys, `NSRunningApplication`).

---

## What it does

- **Close to menu bar, per app.** Pick an app; clicking its red close button sends the app to the menu bar
  (it's hidden, keeps running and gets its own menu bar icon) instead of closing the window. Rules are kept
  by bundle identifier (e.g. `com.apple.TextEdit`) and survive restarts.
- **⌘W to menu bar (optional, on by default).** For those same apps, ⌘W sends the app to the menu bar when
  it would close the app's **last** window (the app has exactly one standard window). With more windows,
  ⌘W closes the window as usual.
- **Right-click minimize to menu bar (optional, on by default).** Right-click the yellow minimize button
  of *any* window to send that app to the menu bar. No rule needed.
- **Per-app keyboard shortcuts (optional).** One global shortcut per rule:

  | The app is… | Pressing the shortcut… |
  |---|---|
  | Hidden in the menu bar | Restores it and brings it to the front |
  | The frontmost app | Sends it to the menu bar |
  | Running but behind other apps (or hidden with ⌘H) | Brings it to the front |
  | Not running | Does nothing (Trayify doesn't launch apps) |

- **One menu bar icon per hidden app**, showing the app's own icon. Hover for its name. **Click** to restore;
  **right-click** (or Control-click) for *Restore ‹app›*, *Restore All Hidden Apps*, *Open Trayify*.
- **Icons remember where you put them.** ⌘-drag an app's menu bar icon (or Trayify's own) to a new spot and it comes back there the next time that app is hidden, even after Trayify restarts.
- **Trayify's own menu bar icon** (a tray symbol). Click to open settings. Right-click for: *Open Trayify*,
  *Restore ‹app›* for each hidden app (or a greyed-out *No hidden apps*), *Restore All*,
  *Right-click minimize sends to menu bar* (checkmark), *Quit Trayify (restores hidden apps)*.
- **Nothing gets lost.** Quitting Trayify restores every app it hid. If Trayify crashes or is killed, a
  small watchdog process (the same binary run as `Trayify --guardian`) restores them right away; if that
  was stopped too, the next launch restores anything left hidden. If an app is shown again some other way
  (you click its Dock icon, ⌘-Tab to it, or it quits), Trayify notices and removes its menu bar icon.
- **Start at login (optional, off by default)**, using the system login items service.
- **Single instance.** Launching Trayify again just opens its settings window.

### Settings window

One scrolling window (about 760×860) that follows light/dark mode:

1. **Accessibility banner** (only while permission is missing), with **Grant Access…** and **Open System Settings** buttons.
2. **General**: *Right-click minimize sends to menu bar*, *⌘W also sends close-to-menu-bar apps to the menu bar*, *Start at login*.
3. **Close-to-menu-bar apps**: one row per rule with icon, name, bundle ID, shortcut button, **✕** (clear shortcut) and **Remove**.
4. **In the menu bar (N)**: hidden apps, each with **Restore**, plus **Restore All**.
5. **Running apps**: regular (Dock) apps sorted by name, with **Refresh**, a *Send to menu bar now* button, and a **Close to menu bar** switch.

Closing the settings window doesn't quit Trayify.

**Recording a shortcut:** click the rule's shortcut button, then press the combination, e.g. ⌃⌥N. **Esc** cancels,
**Delete** clears and **✕** removes it. A shortcut needs at least one modifier (⌘ ⌥ ⌃ ⇧), except **F1–F20**,
which can be used alone. While recording, existing shortcuts are paused. If the combination is already taken
(by another app's global hot key or another rule), the rule shows a **red** message; pick a different one.

---

## Differences from the Windows version, and limits

- **Whole apps, not single windows.** macOS has no public API to hide one window of an app, so Trayify hides the
  whole app (like ⌘H) and restores the whole app. Each hidden *app* gets one menu bar icon.
- **The Dock icon and ⌘-Tab entry stay.** Trayify doesn't remove other apps' Dock or ⌘-Tab icons, and it can't
  without modifying those apps. Clicking the Dock icon or ⌘-Tabbing to a hidden app shows it again (Trayify then
  drops its menu bar icon).
- **⌘W instead of Alt+F4.** Only ⌘W is caught (only for ruled apps, and only with one standard window).
  Still really closes: ⌘Q, *File › Close*, *Window › Close* chosen from the menu,
  and the app closing windows itself.
- **Needs Accessibility permission** to catch clicks on window buttons and ⌘W (see below). Without it, shortcuts,
  menu bar icons, *Send to menu bar now* and the command line still work.
- **Apps that draw their own title bar buttons** (some Electron/Chromium apps) work only if they report standard
  close and minimize buttons to Accessibility (`AXCloseButton` / `AXMinimizeButton`).
- **Menu bar space.** On notched MacBooks, icons that don't fit are hidden behind the notch by macOS.
- **Clicks only with a mouse or trackpad.** Keyboard and VoiceOver activation of the close button isn't caught.
- **Shortcuts are reserved.** While a shortcut is assigned, other apps don't receive that combination.

---

## Permissions: what to grant and exactly what to click

Trayify needs **Accessibility** to see which window button you clicked and to catch that click and ⌘W.

1. Start Trayify. If permission is missing, the settings window shows **Accessibility access needed**.
2. Click **Grant Access…**. macOS shows an *"Trayify" would like to control this computer using accessibility
   features* dialog; click **Open System Settings** (Trayify also opens the right pane for you).
3. In **System Settings › Privacy & Security › Accessibility**, turn on the switch next to **Trayify**.
   If Trayify isn't listed, click **+**, choose `Trayify.app` (e.g. `~/dev/Trayify/macos/build/Trayify.app`)
   and turn it on. You may need to enter your password or use Touch ID.
4. Within a couple of seconds the banner disappears and the clicks start being caught (no restart needed).
   `Trayify --cmd ax` prints `trusted=true tap=true` when everything is active.

If macOS also asks for **Input Monitoring** (System Settings › Privacy & Security › Input Monitoring), turn on
**Trayify** there too.

**After rebuilding:** builds are signed ad-hoc (there's no Developer ID certificate), with a fixed identifier
(`com.gollylabs.trayify`) and designated requirement so the permission has a stable identity to match. Even so,
macOS may treat a rebuilt app as new: if clicks stop being caught after a rebuild (or the banner comes back),
open **Privacy & Security › Accessibility**, select **Trayify**, click **–** to remove it, then add and enable it
again as in step 3. From Terminal you can reset it with `tccutil reset Accessibility com.gollylabs.trayify`.

**Start at login:** if the switch shows *Waiting for approval…*, enable Trayify in
**System Settings › General › Login Items & Extensions**. For the login item to keep working, keep the app in a
fixed location (e.g. copy `Trayify.app` to `/Applications`).

---

## Build

Requirements: macOS 14+, Xcode (or the Command Line Tools) with Swift 6. No third-party tools.

```bash
cd macos
./build.sh            # universal (arm64 + x86_64) release build -> build/Trayify.app
                      # (newer Xcode warns that x86_64 is deprecated; the warning is harmless)
./build.sh --native   # this Mac's architecture only (faster)
./build.sh --debug    # debug build
open build/Trayify.app
```

`build.sh` runs `swift build`, assembles `build/Trayify.app` (Info.plist with `LSUIElement`, bundle ID
`com.gollylabs.trayify`, icon) and signs it ad-hoc. `build/` and `.build/` are git-ignored.
To change the app icon, run `scripts/make-icon.sh` (it converts `windows/src/Trayify/Assets/Trayify.png` into
`Resources/AppIcon.icns`).

You can also open `Package.swift` in Xcode to edit and debug, but use `build.sh` to make the `.app` bundle.

---

## Command line

The app binary doubles as a command-line tool for the running instance (handy for testing and scripts):

```bash
T=build/Trayify.app/Contents/MacOS/Trayify
$T --cmd status                          # pid, permissions, settings, rules, hidden apps, hotkeys
$T --cmd add-rule com.apple.TextEdit     # add a close-to-menu-bar rule
$T --cmd hide com.apple.TextEdit         # send a running app to the menu bar now
$T --cmd list                            # hidden apps
$T --cmd restore com.apple.TextEdit      # or: restore-all
$T --cmd set-hotkey com.apple.TextEdit ctrl+opt+t   # or "none"
$T --cmd set rightclick off              # rightclick | cmdw  on|off
$T --cmd startup on                      # on | off | status
$T --cmd appinfo com.apple.TextEdit      # hidden / active / frontmost state of an app
$T --cmd buttons com.apple.TextEdit      # window close/minimize button positions (needs Accessibility)
$T --cmd probe 20 15                     # what button is at this screen point? (needs Accessibility)
$T --cmd quit                            # quit (restores hidden apps)
$T --recover                             # unhide apps left hidden by a crashed run
```

Commands go over a Unix socket in Trayify's data folder that only your user account can use.
`$T --help` lists everything. Debug builds (`./build.sh --debug`) add `test-input left|right <x> <y>`, `test-input cmdw` and
`test-input stall <s>`, which post synthetic input from Trayify itself to test the event tap end to end; release builds
don't include them.

---

## Files and privacy

Everything stays on your Mac in `~/Library/Application Support/Trayify/`:

- `settings.json`: rules, shortcuts and settings
- `hidden.json`: apps Trayify has currently hidden (pid + bundle ID + launch date), for crash recovery
- `trayify.log`: a local log file
- `trayify.sock`: the local control socket (only while Trayify runs)

No network code, no accounts, no tracking.

---

## How it works

- A `CGEventTap` (session level, on its own thread) watches left/right mouse down/up and key down. On a left
  click it asks Accessibility what's under the pointer (`AXUIElementCopyElementAtPosition`); if it's an
  `AXCloseButton` of an app with a rule, the mouse down and up are swallowed and the app is hidden with
  `NSRunningApplication.hide()`. Right-clicks on `AXMinimizeButton` and ⌘W work the same way. If macOS
  disables the tap for being slow, Trayify re-enables it, and Accessibility calls use a short timeout.
- Hidden apps are tracked with `NSWorkspace` notifications (unhide, terminate) plus a 1.5-second check.
- Shortcuts use Carbon `RegisterEventHotKey` (exclusive), which needs no extra permission.
- Restoring uses `unhide()` and `activate()`; Trayify yields activation to the app first.
- Start at login uses `SMAppService.mainApp`.
