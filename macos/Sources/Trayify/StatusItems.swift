import AppKit

/// NSMenuItem that runs a closure.
@MainActor
final class ActionMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, enabled: Bool = true, checked: Bool = false, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self
        isEnabled = enabled
        state = checked ? .on : .off
    }

    required init(coder: NSCoder) { fatalError("not used") }

    @objc private func run() { handler() }
}

/// Trayify's own menu bar item plus one item per hidden app.
@MainActor
final class StatusController: NSObject {
    private let main: NSStatusItem
    private var appItems: [pid_t: NSStatusItem] = [:]
    private unowned let core: AppCore

    init(core: AppCore) {
        self.core = core
        StatusController.prepareAutosave("TrayifyMain")
        main = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()
        if let button = main.button {
            let img = NSImage(systemSymbolName: "tray.and.arrow.down", accessibilityDescription: "Trayify")
            img?.isTemplate = true
            button.image = img
            button.target = self
            button.action = #selector(mainClicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        main.autosaveName = "TrayifyMain"
        updateMainTip()
    }

    var appItemCount: Int { appItems.count }

    private static func isRightClick() -> Bool {
        guard let e = NSApp.currentEvent else { return false }
        return e.type == .rightMouseUp || e.type == .rightMouseDown || e.modifierFlags.contains(.control)
    }

    private func pop(_ menu: NSMenu, on item: NSStatusItem) {
        item.menu = menu
        item.button?.performClick(nil)
        item.menu = nil
    }

    @objc private func mainClicked(_ sender: NSStatusBarButton) {
        if Self.isRightClick() { pop(mainMenu(), on: main) } else { core.showSettings() }
    }

    private func mainMenu() -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.addItem(ActionMenuItem("Open Trayify") { [weak self] in self?.core.showSettings() })
        menu.addItem(.separator())
        let hidden = core.hidden.hidden
        if hidden.isEmpty {
            menu.addItem(ActionMenuItem("No hidden apps", enabled: false) {})
        }
        for h in hidden {
            let pid = h.pid
            let item = ActionMenuItem("Restore \(h.name)") { [weak self] in self?.core.hidden.restore(pid: pid) }
            item.image = Self.icon(for: h.app, size: 16)
            menu.addItem(item)
        }
        menu.addItem(ActionMenuItem("Restore All", enabled: !hidden.isEmpty) { [weak self] in self?.core.hidden.restoreAll() })
        menu.addItem(.separator())
        menu.addItem(ActionMenuItem("Right-click minimize sends to menu bar", checked: core.settings.rightClickMinimize) { [weak self] in
            guard let self else { return }
            self.core.update { $0.rightClickMinimize.toggle() }
        })
        menu.addItem(.separator())
        menu.addItem(ActionMenuItem("Quit Trayify (restores hidden apps)") { [weak self] in self?.core.quit() })
        return menu
    }

    func updateMainTip() {
        let n = core.hidden.hidden.count
        main.button?.toolTip = n == 0 ? "Trayify" : "Trayify – \(n) hidden app\(n == 1 ? "" : "s")"
    }

    static func icon(for app: NSRunningApplication, size: CGFloat) -> NSImage {
        let src = app.icon ?? NSImage(systemSymbolName: "app", accessibilityDescription: nil) ?? NSImage()
        let img = src.copy() as! NSImage
        img.size = NSSize(width: size, height: size)
        return img
    }

    func addAppItem(_ h: HiddenApp) {
        removeAppItem(pid: h.pid)
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        // A stable name per app makes macOS remember where the user ⌘-dragged this app's icon.
        // (A second hidden instance of the same app gets a numbered name so names stay unique.)
        let base = "Trayify.app.\(h.bundleId)"
        let used = Set(appItems.values.compactMap(\.autosaveName))
        var name = base, n = 2
        while used.contains(name) { name = "\(base).\(n)"; n += 1 }
        Self.prepareAutosave(name)
        item.autosaveName = name
        if !item.isVisible { item.isVisible = true } // the item is only removed (never hidden) by us
        if let button = item.button {
            button.image = Self.icon(for: h.app, size: 18)
            button.imageScaling = .scaleProportionallyDown
            button.toolTip = "\(h.name) – click to restore"
            button.setAccessibilityLabel("Restore \(h.name)")
            button.target = self
            button.action = #selector(appClicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
            button.tag = Int(h.pid)
        }
        appItems[h.pid] = item
        updateMainTip()
    }

    func removeAppItem(pid: pid_t) {
        if let item = appItems.removeValue(forKey: pid) { Self.removeKeepingPosition(item) }
        updateMainTip()
    }

    // AppKit deletes "NSStatusItem Preferred Position <autosaveName>" when an item is removed (on hide→restore),
    // so Trayify keeps its own copy of each position and puts it back before the item is created again.
    private static let positionsKey = "TrayifyStatusItemPositions"
    private static func positionKey(_ name: String) -> String { "NSStatusItem Preferred Position \(name)" }

    /// Before creating an item with this autosave name: restore the remembered position if AppKit dropped it.
    static func prepareAutosave(_ name: String) {
        let d = UserDefaults.standard
        guard d.object(forKey: positionKey(name)) == nil,
              let saved = (d.dictionary(forKey: positionsKey) ?? [:])[name] else { return }
        d.set(saved, forKey: positionKey(name))
    }

    /// Remembers an item's current position (if the user has moved it), then removes it.
    private static func removeKeepingPosition(_ item: NSStatusItem) {
        let d = UserDefaults.standard
        let name = item.autosaveName as String
        if let pos = d.object(forKey: positionKey(name)) {
            var all = d.dictionary(forKey: positionsKey) ?? [:]
            all[name] = pos
            d.set(all, forKey: positionsKey)
        }
        NSStatusBar.system.removeStatusItem(item)
    }

    @objc private func appClicked(_ sender: NSStatusBarButton) {
        let pid = pid_t(sender.tag)
        guard let h = core.hidden.find(pid: pid), let item = appItems[pid] else { return }
        if Self.isRightClick() {
            let menu = NSMenu()
            menu.autoenablesItems = false
            menu.addItem(ActionMenuItem("Restore \(h.name)") { [weak self] in self?.core.hidden.restore(pid: pid) })
            menu.addItem(ActionMenuItem("Restore All Hidden Apps") { [weak self] in self?.core.hidden.restoreAll() })
            menu.addItem(.separator())
            menu.addItem(ActionMenuItem("Open Trayify") { [weak self] in self?.core.showSettings() })
            pop(menu, on: item)
        } else {
            core.hidden.restore(pid: pid)
        }
    }

    func removeAll() {
        for pid in Array(appItems.keys) { removeAppItem(pid: pid) }
        Self.removeKeepingPosition(main)
    }
}
