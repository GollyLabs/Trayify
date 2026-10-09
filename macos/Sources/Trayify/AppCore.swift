import AppKit
import ApplicationServices
import ServiceManagement

/// Wires settings, the event tap, hidden-app bookkeeping, menu bar items, hotkeys and the control socket together.
@MainActor
final class AppCore {
    private(set) var settings = SettingsStore.load()
    let hidden = HiddenAppManager()
    let hotkeys = HotkeyManager()
    private(set) var interceptor: InputInterceptor!
    private(set) var status: StatusController!
    private(set) var model: SettingsModel!
    private var windowController: SettingsWindowController?
    private var ipc: IpcServer?
    private var timer: Timer?
    private var observers: [NSObjectProtocol] = []
    private var guardian: Process?
    private var shutDown = false

    /// Called when settings, hidden apps, hotkey status or permissions change (UI refresh).
    var onChange: (() -> Void)?

    func start(spawnGuardian: Bool) {
        let recovered = Recovery.restoreFromFile("startup")
        if recovered > 0 { Log.warn("Restored \(recovered) app(s) left hidden by a previous run") }

        model = SettingsModel(core: self)
        status = StatusController(core: self)

        hidden.onHidden = { [weak self] h in self?.status.addAppItem(h); self?.pollBadges() }
        hidden.onRemoved = { [weak self] h in self?.status.removeAppItem(pid: h.pid) }
        hidden.onChanged = { [weak self] in self?.changed() }

        hotkeys.onPressed = { [weak self] bid in
            guard let self else { return }
            Log.info("Hotkey for \(bid): \(self.toggleApp(bid))")
        }
        hotkeys.onStatusChanged = { [weak self] in self?.changed() }

        interceptor = InputInterceptor { pid, reason in
            DispatchQueue.main.async {
                MainActor.assumeIsolated { _ = AppDelegate.core?.hide(pid: pid, reason: reason) }
            }
        }
        interceptor.setFrontmost(NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0)
        let nc = NSWorkspace.shared.notificationCenter
        observers.append(nc.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let pid = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier ?? 0
            MainActor.assumeIsolated { self?.interceptor.setFrontmost(pid) }
        })
        observers.append(nc.addObserver(forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let pid = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier ?? 0
            MainActor.assumeIsolated { self?.interceptor.forget(pid: pid) }
        })

        applySettings()
        if interceptor.start() == false {
            Log.warn("Event tap not available yet (Accessibility permission missing); will retry")
        }

        // Every 1.5 s: drop stale hidden entries, retry the event tap once permission is granted, refresh permission UI.
        timer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }

        ipc = IpcServer { line in
            DispatchQueue.main.sync { MainActor.assumeIsolated { AppDelegate.core?.handleCommand(line) ?? "shutting down" } }
        }
        ipc?.start()

        if spawnGuardian { startGuardian() }
        Log.info("Trayify started (pid \(getpid()), tap=\(interceptor.isRunning), ax=\(AXIsProcessTrusted()), rules=\(settings.rules.count))")
    }

    private var lastTrusted: Bool?
    private var tickCount = 0
    private var badgePollRunning = false

    private func tick() {
        hidden.prune()
        tickCount += 1
        if tickCount % 2 == 0 { pollBadges() } // every ~3 s, only while something is hidden
        let trusted = AXIsProcessTrusted()
        if trusted && !interceptor.isRunning {
            if interceptor.start() { Log.info("Accessibility granted; event tap started") }
        }
        if trusted != lastTrusted { lastTrusted = trusted; changed() }
    }

    /// Reads Dock badges for hidden apps (Accessibility, off the main thread) and updates their menu bar icons.
    func pollBadges() {
        guard settings.showBadges, !hidden.hidden.isEmpty, !badgePollRunning, AXIsProcessTrusted() else { return }
        badgePollRunning = true
        DispatchQueue.global(qos: .utility).async {
            let entries = DockBadges.read()
            DispatchQueue.main.async {
                MainActor.assumeIsolated { AppDelegate.core?.applyBadges(entries) }
            }
        }
    }

    private func applyBadges(_ entries: [DockBadges.Entry]?) {
        badgePollRunning = false
        guard let entries, settings.showBadges else { return }
        var any = false
        for h in hidden.hidden {
            let b = DockBadges.badge(in: entries, bundleURL: h.app.bundleURL, name: h.name)
            if b != h.badge {
                h.badge = b
                status.updateAppItem(h)
                Log.info("Badge for \(h.bundleId): \(b.map { "'\($0)'" } ?? "none")")
                any = true
            }
        }
        if any { changed() }
    }

    func setShowBadges(_ on: Bool) {
        update { $0.showBadges = on }
        if !on { for h in hidden.hidden { h.badge = nil; status.updateAppItem(h) } } else { pollBadges() }
        changed()
    }

    @discardableResult
    func setFadeDock(_ on: Bool) -> String? {
        let err = DockFade.set(on)
        changed()
        return err
    }

    private func changed() {
        model?.reload()
        onChange?()
    }

    private func applySettings() {
        interceptor?.configure(rules: Set(settings.rules.map(\.bundleId)),
                               rightClickMinimize: settings.rightClickMinimize,
                               cmdW: settings.cmdWToMenuBar)
        hotkeys.apply(settings.rules)
    }

    func update(_ change: (inout AppSettings) -> Void) {
        change(&settings)
        SettingsStore.save(settings)
        applySettings()
        changed()
    }

    // MARK: - Rules

    func hasRule(_ bundleId: String) -> Bool { settings.rules.contains { $0.bundleId == bundleId } }

    func addRule(bundleId: String, name: String? = nil, path: String? = nil) {
        guard !bundleId.isEmpty, !hasRule(bundleId) else { return }
        var name = name, path = path
        if name == nil || path == nil, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleId) {
            path = path ?? url.path
            name = name ?? (Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String)
                ?? (Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleName") as? String)
                ?? url.deletingPathExtension().lastPathComponent
        }
        update { $0.rules.append(AppRule(bundleId: bundleId, name: name, path: path)) }
        Log.info("Rule added: \(bundleId)")
    }

    func removeRule(_ bundleId: String) {
        update { $0.rules.removeAll { $0.bundleId == bundleId } }
        Log.info("Rule removed: \(bundleId)")
    }

    func setHotkey(_ bundleId: String, _ hk: Hotkey?) {
        update { s in
            for i in s.rules.indices where s.rules[i].bundleId == bundleId { s.rules[i].hotkey = hk }
        }
        Log.info("Hotkey for \(bundleId) set to '\(hk?.spec ?? "none")'")
    }

    // MARK: - Hiding

    static func runningApp(_ bundleId: String) -> NSRunningApplication? {
        let apps = NSRunningApplication.runningApplications(withBundleIdentifier: bundleId).filter { !$0.isTerminated }
        return apps.first { $0.activationPolicy == .regular } ?? apps.first
    }

    @discardableResult
    func hide(pid: pid_t, reason: HideReason) -> Bool {
        guard let app = NSRunningApplication(processIdentifier: pid) else { return false }
        return hidden.hide(app, reason: reason) != nil
    }

    /// Per-rule shortcut:
    ///  hidden -> restore; frontmost -> hide; running but behind -> bring to front; not running -> nothing.
    func toggleApp(_ bundleId: String) -> String {
        if let h = hidden.hidden.last(where: { $0.bundleId == bundleId }) {
            return hidden.restore(pid: h.pid) ? "restored" : "restore-failed"
        }
        guard let app = Self.runningApp(bundleId) else { return "not-running" }
        if NSWorkspace.shared.frontmostApplication?.processIdentifier == app.processIdentifier && !app.isHidden {
            return hidden.hide(app, reason: .hotkey) != nil ? "hidden" : "hide-failed"
        }
        Activation.bringToFront(app)
        return "focused"
    }

    // MARK: - Login item

    var startAtLogin: Bool { SMAppService.mainApp.status == .enabled }
    var loginItemNote: String? {
        switch SMAppService.mainApp.status {
        case .requiresApproval: return "Waiting for approval in System Settings > General > Login Items."
        default: return nil
        }
    }

    @discardableResult
    func setStartAtLogin(_ on: Bool) -> String? {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            Log.info("Start at login \(on ? "enabled" : "disabled") (status \(SMAppService.mainApp.status.rawValue))")
            changed()
            return nil
        } catch {
            Log.error("Start at login change failed: \(error)")
            changed()
            return error.localizedDescription
        }
    }

    // MARK: - Window, guardian, quit

    func showSettings() {
        if windowController == nil { windowController = SettingsWindowController(model: model) }
        model.reload(refreshRunning: true)
        windowController?.show()
    }

    private func startGuardian() {
        guard let exe = Bundle.main.executableURL else { return }
        let p = Process()
        p.executableURL = exe
        p.arguments = ["--guardian", String(getpid())]
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run(); guardian = p; Log.info("Guardian started (pid \(p.processIdentifier))") }
        catch { Log.error("Failed to start guardian: \(error)") }
    }

    func quit() {
        shutdown()
        NSApp.terminate(nil)
    }

    /// Clean shutdown: stop the tap, restore everything, remove items, clear hidden.json.
    func shutdown() {
        if shutDown { return }
        shutDown = true
        interceptor?.stop()
        let n = hidden.restoreAll(activateLast: false)
        Log.info("Shutdown: restored \(n) app(s)")
        Recovery.write([])
        status?.removeAll()
        ipc?.stop()
        timer?.invalidate()
    }

    // MARK: - Control commands

    static let commandHelp = """
    commands: ping | show | quit | status | list | running | rules | add-rule <bundleid> | remove-rule <bundleid> |
      hide <bundleid> | restore <bundleid> | restore-all | set rightclick|cmdw|badges|fadedock on|off | dock-badges | startup on|off|status |
      hotkeys | set-hotkey <bundleid> <combo|none> (e.g. ctrl+opt+n) | toggle-app <bundleid> | appinfo <bundleid> | buttons <bundleid> | ax | probe <x> <y>
    """

    func handleCommand(_ line: String) -> String {
        let parts = line.split(separator: " ", maxSplits: 1).map(String.init)
        let cmd = parts.first?.lowercased() ?? ""
        let arg = parts.count > 1 ? parts[1].trimmingCharacters(in: .whitespaces) : ""
        switch cmd {
        case "ping": return "pong \(getpid())"
        case "show": showSettings(); return "ok"
        case "quit":
            DispatchQueue.main.async { MainActor.assumeIsolated { AppDelegate.core?.quit() } }
            return "ok"
        case "list":
            return hidden.hidden.map { "\($0.pid)\t\($0.bundleId)\t\($0.reason.rawValue)\t\($0.name)\tbadge=\($0.badge.map { "'\($0)'" } ?? "none")" }.joined(separator: "\n")
        case "dock-badges":
            guard let entries = DockBadges.read() else { return "can't read the Dock (ax=\(AXIsProcessTrusted()))" }
            return entries.map { "\($0.title ?? "?")\t\($0.badge.map { "'\($0)'" } ?? "-")\t\($0.url?.path ?? "")" }.joined(separator: "\n")
        case "running":
            return SettingsModel.runningApps().map { "\($0.pid)\t\($0.bundleId)\t\($0.name)" }.joined(separator: "\n")
        case "rules":
            return settings.rules.map { "\($0.bundleId)\t\($0.hotkey?.spec ?? "-")\t\($0.displayName)" }.joined(separator: "\n")
        case "add-rule":
            guard !arg.isEmpty else { return "usage: add-rule <bundleid>" }
            addRule(bundleId: arg); return "ok"
        case "remove-rule":
            guard hasRule(arg) else { return "no such rule" }
            removeRule(arg); return "ok"
        case "hide":
            guard let app = Self.runningApp(arg) else { return "not running" }
            return hidden.hide(app, reason: .command) != nil ? "ok" : "failed"
        case "restore":
            guard let h = hidden.hidden.last(where: { $0.bundleId == arg || String($0.pid) == arg }) else { return "not hidden" }
            return hidden.restore(pid: h.pid) ? "ok" : "failed"
        case "restore-all": return "restored \(hidden.restoreAll())"
        case "set":
            let kv = arg.split(separator: " ").map(String.init)
            guard kv.count == 2 else { return "usage: set rightclick|cmdw|badges|fadedock on|off" }
            let on = ["on", "true", "1", "yes"].contains(kv[1].lowercased())
            switch kv[0].lowercased() {
            case "rightclick": update { $0.rightClickMinimize = on }
            case "cmdw": update { $0.cmdWToMenuBar = on }
            case "badges": setShowBadges(on)
            case "fadedock": if let err = setFadeDock(on) { return err }
            default: return "unknown setting"
            }
            return "ok"
        case "startup":
            var err: String?
            if arg == "on" { err = setStartAtLogin(true) } else if arg == "off" { err = setStartAtLogin(false) }
            return "enabled=\(startAtLogin) status=\(SMAppService.mainApp.status.rawValue)" + (err.map { " error=\($0)" } ?? "")
        case "status":
            return """
            pid=\(getpid()) version=\(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?") \
            ax=\(AXIsProcessTrusted()) tap=\(interceptor.isRunning) intercepts=\(interceptor.interceptCount)
            rightclick=\(settings.rightClickMinimize) cmdw=\(settings.cmdWToMenuBar) startAtLogin=\(startAtLogin) badges=\(settings.showBadges) fadedock=\(DockFade.isEnabled)
            rules=\(settings.rules.map(\.bundleId).joined(separator: ","))
            hidden=\(hidden.hidden.count) appStatusItems=\(status.appItemCount) guardian=\(guardian?.isRunning == true ? String(guardian!.processIdentifier) : "none")
            window=\(windowController?.isVisible == true ? "visible" : "hidden")
            hotkeys:
            \(hotkeys.describe())
            """
        case "hotkeys": return hotkeys.describe()
        case "set-hotkey":
            guard let cut = arg.lastIndex(of: " ") else { return "usage: set-hotkey <bundleid> <combo|none>" }
            let bid = String(arg[..<cut]).trimmingCharacters(in: .whitespaces)
            guard hasRule(bid) else { return "no such rule" }
            switch Hotkey.parse(String(arg[arg.index(after: cut)...])) {
            case .failure(let e): return "bad combo: \(e.message)"
            case .success(let hk):
                setHotkey(bid, hk)
                return hotkeys.error(for: bid) ?? "ok \(hk?.spec ?? "none")"
            }
        case "toggle-app": return toggleApp(arg)
        case "buttons":
            guard let app = Self.runningApp(arg) else { return "not running" }
            return AX.describeButtons(pid: app.processIdentifier)
#if DEBUG
        case "test-input":
            // DEBUG builds only: post synthetic input from this (Accessibility-trusted) process so the event tap
            // can be exercised end to end. Never compiled into release builds.
            return TestInput.run(arg)
        case "snapshot-item":
            // DEBUG: write a hidden app's current menu bar icon image to <data dir>/item-<bundleid>.png.
            guard let h = hidden.hidden.last(where: { $0.bundleId == arg }), let img = status.image(for: h.pid),
                  let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) else { return "no item" }
            let url = Paths.dataDir.appendingPathComponent("item-\(arg).png")
            do { try png.write(to: url) } catch { return "write failed: \(error)" }
            return "\(url.path) \(Int(img.size.width))x\(Int(img.size.height)) tooltip=\(status.tooltip(for: h.pid) ?? "")"
#endif
        case "appinfo":
            guard let app = Self.runningApp(arg) else { return "not running" }
            let front = NSWorkspace.shared.frontmostApplication
            return "pid=\(app.processIdentifier) hidden=\(app.isHidden) active=\(app.isActive) frontmost=\(front?.bundleIdentifier ?? "?") tracked=\(hidden.find(pid: app.processIdentifier) != nil)"
        case "ax": return "trusted=\(AXIsProcessTrusted()) tap=\(interceptor.isRunning)"
        case "probe":
            let xy = arg.split(separator: " ").compactMap { Double($0) }
            guard xy.count == 2 else { return "usage: probe <x> <y>" }
            let sw = AXUIElementCreateSystemWide()
            guard let r = AX.classify(CGPoint(x: xy[0], y: xy[1]), systemWide: sw) else { return "nothing (ax=\(AXIsProcessTrusted()))" }
            let bid = NSRunningApplication(processIdentifier: r.pid)?.bundleIdentifier ?? "?"
            return "pid=\(r.pid) bundle=\(bid) button=\(r.button.rawValue) rule=\(hasRule(bid))"
        default:
            return "unknown command. " + Self.commandHelp
        }
    }
}
