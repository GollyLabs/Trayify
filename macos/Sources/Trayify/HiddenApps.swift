import AppKit

enum HideReason: String, Codable, Sendable {
    case closeButton, minimizeRightClick, cmdW, hotkey, manual, command

    var label: String {
        switch self {
        case .closeButton: return "closed to menu bar"
        case .minimizeRightClick: return "right-click minimize"
        case .cmdW: return "⌘W"
        case .hotkey: return "shortcut"
        case .manual, .command: return "sent to menu bar"
        }
    }
}

/// Persisted record of an app Trayify hid (used for crash/kill recovery).
struct HiddenRecord: Codable, Sendable {
    var pid: Int32
    var bundleId: String
    var name: String
    var launchDate: Double?
}

@MainActor
final class HiddenApp {
    let app: NSRunningApplication
    let pid: pid_t
    let bundleId: String
    let name: String
    let reason: HideReason
    let hiddenAt = Date()
    /// The app's Dock badge while hidden (nil = none, "" or text = badge present).
    var badge: String?

    /// "3", "•" (non-numeric badge) or nil.
    var badgeText: String? { badge.map { BadgeImage.text($0) ?? "•" } }

    init(app: NSRunningApplication, reason: HideReason) {
        self.app = app
        pid = app.processIdentifier
        bundleId = app.bundleIdentifier ?? "pid-\(app.processIdentifier)"
        name = app.localizedName ?? bundleId
        self.reason = reason
    }

    var record: HiddenRecord {
        HiddenRecord(pid: pid, bundleId: bundleId, name: name, launchDate: app.launchDate?.timeIntervalSince1970)
    }
}

/// Restores apps listed in hidden.json by a previous Trayify instance that died.
enum Recovery {
    static func read() -> [HiddenRecord] {
        guard let data = try? Data(contentsOf: Paths.hiddenFile) else { return [] }
        do { return try JSONDecoder().decode([HiddenRecord].self, from: data) }
        catch { Log.error("Failed to read hidden.json: \(error)"); return [] }
    }

    static func write(_ records: [HiddenRecord]) {
        do { try Paths.writeAtomically(try JSONEncoder().encode(records), to: Paths.hiddenFile) }
        catch { Log.error("Failed to write hidden.json: \(error)") }
    }

    /// Unhides every still-hidden app from the file (validated by pid + bundle id + launch date), then clears it.
    @discardableResult
    static func restoreFromFile(_ why: String) -> Int {
        let records = read()
        var restored = 0
        for r in records {
            guard let app = NSRunningApplication(processIdentifier: r.pid), !app.isTerminated,
                  app.bundleIdentifier == r.bundleId else { continue }
            if let want = r.launchDate, let have = app.launchDate?.timeIntervalSince1970, abs(want - have) > 1 { continue }
            if app.isHidden {
                app.unhide()
                restored += 1
            }
            Log.info("Recovery (\(why)): restored \(r.bundleId) pid=\(r.pid)")
        }
        if !records.isEmpty { write([]) }
        return restored
    }
}

/// Owns the set of apps Trayify has hidden. Main thread only.
@MainActor
final class HiddenAppManager {
    private(set) var hidden: [HiddenApp] = []
    var onHidden: ((HiddenApp) -> Void)?
    var onRemoved: ((HiddenApp) -> Void)?
    var onChanged: (() -> Void)?
    private var observers: [NSObjectProtocol] = []

    init() {
        let nc = NSWorkspace.shared.notificationCenter
        observers.append(nc.addObserver(forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            guard let pid = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier else { return }
            MainActor.assumeIsolated { self?.dropIfTracked(pid, why: "quit") }
        })
        observers.append(nc.addObserver(forName: NSWorkspace.didUnhideApplicationNotification, object: nil, queue: .main) { [weak self] note in
            guard let pid = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier else { return }
            MainActor.assumeIsolated { self?.dropIfTracked(pid, why: "was shown by the user or the app") }
        })
    }

    func find(pid: pid_t) -> HiddenApp? { hidden.first { $0.pid == pid } }

    @discardableResult
    func hide(_ app: NSRunningApplication, reason: HideReason) -> HiddenApp? {
        if app.processIdentifier == getpid() { Log.warn("Refusing to hide Trayify itself"); return nil }
        if app.isTerminated { return nil }
        if let existing = find(pid: app.processIdentifier) {
            if !app.isHidden { app.hide() }
            return existing
        }
        let h = HiddenApp(app: app, reason: reason)
        // hide() can report false even though the app does hide (the request is asynchronous), so the entry
        // is tracked either way; prune() drops it if the app is still visible a couple of seconds later.
        if !app.hide() { Log.info("hide() returned false for \(h.bundleId); tracking anyway") }
        hidden.append(h)
        persist()
        Log.info("Hid \(h.bundleId) '\(h.name)' pid=\(h.pid) reason=\(reason.rawValue)")
        onHidden?(h)
        onChanged?()
        return h
    }

    @discardableResult
    func restore(pid: pid_t, activate: Bool = true) -> Bool {
        guard let h = find(pid: pid) else { return false }
        remove(h)
        if h.app.isTerminated { Log.info("\(h.bundleId) is no longer running"); return false }
        h.app.unhide()
        if activate { Activation.bringToFront(h.app) }
        Log.info("Restored \(h.bundleId) pid=\(h.pid)")
        return true
    }

    @discardableResult
    func restoreAll(activateLast: Bool = true) -> Int {
        let all = hidden
        for (i, h) in all.enumerated() { restore(pid: h.pid, activate: activateLast && i == all.count - 1) }
        persist()
        return all.count
    }

    /// Drops entries whose app quit or was shown again outside Trayify.
    func prune() {
        for h in hidden {
            if h.app.isTerminated { dropIfTracked(h.pid, why: "quit") }
            else if !h.app.isHidden && Date().timeIntervalSince(h.hiddenAt) > 2 {
                dropIfTracked(h.pid, why: "is visible again")
            }
        }
    }

    private func dropIfTracked(_ pid: pid_t, why: String) {
        guard let h = find(pid: pid) else { return }
        Log.info("Hidden app \(h.bundleId) pid=\(pid) \(why); removing its menu bar item")
        remove(h)
    }

    private func remove(_ h: HiddenApp) {
        guard let i = hidden.firstIndex(where: { $0 === h }) else { return }
        hidden.remove(at: i)
        persist()
        onRemoved?(h)
        onChanged?()
    }

    func persist() { Recovery.write(hidden.map(\.record)) }
}

@MainActor
enum Activation {
    /// Brings an app to the front. Trayify is a background (menu bar) app, so it yields activation first.
    static func bringToFront(_ app: NSRunningApplication) {
        if app.isHidden { app.unhide() }
        NSApp.yieldActivation(to: app)
        if !app.activate(options: [.activateAllWindows]) {
            Log.warn("activate() refused for \(app.bundleIdentifier ?? "?")")
        }
    }
}
