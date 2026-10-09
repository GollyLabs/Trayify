import AppKit
import ApplicationServices
import SwiftUI

struct RunningAppItem: Identifiable {
    let pid: pid_t
    let name: String
    let bundleId: String
    let icon: NSImage
    var id: pid_t { pid }
}

struct HiddenAppItem: Identifiable {
    let pid: pid_t
    let name: String
    let details: String
    let icon: NSImage
    var id: pid_t { pid }
}

struct RuleRow: Identifiable {
    let bundleId: String
    let name: String
    let icon: NSImage
    let hotkey: Hotkey?
    let error: String?
    var id: String { bundleId }
}

/// Observable state for the settings window.
@MainActor
final class SettingsModel: ObservableObject {
    unowned let core: AppCore

    @Published var rightClickMinimize = true
    @Published var cmdW = true
    @Published var startAtLogin = false
    @Published var showBadges = true
    @Published var fadeDock = false
    @Published var loginNote: String?
    @Published var axTrusted = AXIsProcessTrusted()
    @Published var tapRunning = false
    @Published var rules: [RuleRow] = []
    @Published var hidden: [HiddenAppItem] = []
    @Published var running: [RunningAppItem] = []
    @Published var ruleIds: Set<String> = []
    @Published var recording: String?
    @Published var recorderHint = ""

    private var keyMonitor: Any?
    private var iconCache: [String: NSImage] = [:]

    init(core: AppCore) { self.core = core }

    static func runningApps() -> [RunningAppItem] {
        NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && !$0.isTerminated && $0.processIdentifier > 0 && $0.processIdentifier != getpid() && $0.bundleIdentifier != nil }
            .map { RunningAppItem(pid: $0.processIdentifier, name: $0.localizedName ?? $0.bundleIdentifier!,
                                  bundleId: $0.bundleIdentifier!, icon: StatusController.icon(for: $0, size: 32)) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    private func icon(bundleId: String, path: String?) -> NSImage {
        if let i = iconCache[bundleId] { return i }
        let img: NSImage
        if let path, FileManager.default.fileExists(atPath: path) {
            img = NSWorkspace.shared.icon(forFile: path)
        } else if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleId) {
            img = NSWorkspace.shared.icon(forFile: url.path)
        } else {
            img = NSImage(systemSymbolName: "app.dashed", accessibilityDescription: nil) ?? NSImage()
        }
        img.size = NSSize(width: 32, height: 32)
        iconCache[bundleId] = img
        return img
    }

    func reload(refreshRunning: Bool = false) {
        let s = core.settings
        rightClickMinimize = s.rightClickMinimize
        cmdW = s.cmdWToMenuBar
        showBadges = s.showBadges
        fadeDock = DockFade.isEnabled
        startAtLogin = core.startAtLogin
        loginNote = core.loginItemNote
        axTrusted = AXIsProcessTrusted()
        tapRunning = core.interceptor?.isRunning ?? false
        ruleIds = Set(s.rules.map(\.bundleId))
        rules = s.rules.map {
            RuleRow(bundleId: $0.bundleId, name: $0.displayName, icon: icon(bundleId: $0.bundleId, path: $0.path),
                    hotkey: $0.hotkey, error: core.hotkeys.error(for: $0.bundleId))
        }
        let f = DateFormatter()
        f.timeStyle = .short
        hidden = core.hidden.hidden.map {
            HiddenAppItem(pid: $0.pid, name: $0.name,
                          details: "\($0.bundleId)  ·  \($0.reason.label)  ·  \(f.string(from: $0.hiddenAt))" + ($0.badgeText.map { "  ·  \($0 == "•" ? "badge" : "\($0) unread")" } ?? ""),
                          icon: StatusController.icon(for: $0.app, size: 32))
        }
        if refreshRunning || running.isEmpty { refreshRunning_() } else {
            // Keep the list but drop apps that quit.
            running.removeAll { NSRunningApplication(processIdentifier: $0.pid)?.isTerminated ?? true }
        }
    }

    func refreshRunning_() {
        iconCache.removeAll()
        running = Self.runningApps()
    }

    // MARK: actions

    func setRightClick(_ on: Bool) { core.update { $0.rightClickMinimize = on } }
    func setCmdW(_ on: Bool) { core.update { $0.cmdWToMenuBar = on } }
    func setShowBadges(_ on: Bool) { core.setShowBadges(on) }
    func setFadeDock(_ on: Bool) { core.setFadeDock(on); fadeDock = DockFade.isEnabled }
    func setStartAtLogin(_ on: Bool) {
        if let err = core.setStartAtLogin(on) { loginNote = "Couldn't change the login item: \(err)" }
    }

    func setRule(_ item: RunningAppItem, _ on: Bool) {
        if on {
            let app = NSRunningApplication(processIdentifier: item.pid)
            core.addRule(bundleId: item.bundleId, name: item.name, path: app?.bundleURL?.path)
        } else {
            core.removeRule(item.bundleId)
        }
    }

    func sendNow(_ item: RunningAppItem) { _ = core.hide(pid: item.pid, reason: .manual) }
    func restore(_ pid: pid_t) { core.hidden.restore(pid: pid) }
    func restoreAll() { core.hidden.restoreAll() }
    func removeRule(_ bundleId: String) { core.removeRule(bundleId) }
    func clearHotkey(_ bundleId: String) { stopRecording(); core.setHotkey(bundleId, nil) }

    func requestAccessibility() {
        let key = "AXTrustedCheckOptionPrompt" // kAXTrustedCheckOptionPrompt (a global var, not concurrency-safe in Swift 6)
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
        openAccessibilitySettings()
    }

    func openAccessibilitySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: shortcut recorder

    func startRecording(_ bundleId: String) {
        stopRecording()
        recording = bundleId
        recorderHint = "Type the new shortcut. Esc cancels, Delete clears."
        core.hotkeys.suspend() // so pressing the current combo doesn't trigger it
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { [weak self] e in
            guard let self, self.recording != nil else { return e }
            return self.handleRecorderKey(e)
        }
    }

    func stopRecording() {
        if let m = keyMonitor { NSEvent.removeMonitor(m); keyMonitor = nil }
        if recording != nil {
            recording = nil
            recorderHint = ""
            core.hotkeys.resume()
        }
    }

    private func handleRecorderKey(_ e: NSEvent) -> NSEvent? {
        guard let bid = recording else { return e }
        if e.type == .flagsChanged { return nil }   // wait for the actual key
        if e.isARepeat { return nil }
        let mods = Hotkey.carbonModifiers(e.modifierFlags)
        let code = UInt32(e.keyCode)
        if mods == 0 && code == 0x35 { stopRecording(); return nil }                      // Esc
        if mods == 0 && (code == 0x33 || code == 0x75) { clearHotkey(bid); return nil }  // Delete
        let hk = Hotkey(keyCode: code, modifiers: mods)
        guard hk.isValid else {
            recorderHint = "Use at least one modifier (⌘, ⌥, ⌃ or ⇧), or an F-key (F1–F20)."
            return nil
        }
        stopRecording()
        core.setHotkey(bid, hk)   // failures show up as the rule's red message
        return nil
    }
}
