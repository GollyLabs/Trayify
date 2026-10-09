import AppKit
import ApplicationServices

/// Reads Dock tile badges (e.g. unread counts) through Accessibility: the Dock's AXList holds one
/// AXDockItem per app, and an item's AXStatusLabel is its badge text. Generic for every app.
enum DockBadges {
    struct Entry: Sendable {
        let title: String?
        let url: URL?
        let badge: String?
    }

    /// All Dock items with their badge (nil = no badge). Safe to call off the main thread.
    static func read() -> [Entry]? {
        guard let dock = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.dock").first else { return nil }
        let app = AXUIElementCreateApplication(dock.processIdentifier)
        AXUIElementSetMessagingTimeout(app, 0.5)
        guard let lists = children(app) else { return nil }
        var out: [Entry] = []
        for list in lists where AX.string(list, kAXRoleAttribute) == kAXListRole {
            for item in children(list) ?? [] where AX.string(item, kAXRoleAttribute) == "AXDockItem" {
                var urlRef: CFTypeRef?
                var url: URL?
                if AXUIElementCopyAttributeValue(item, kAXURLAttribute as CFString, &urlRef) == .success, let urlRef {
                    url = (urlRef as? URL) ?? (urlRef as? NSURL).map { $0 as URL }
                }
                var badgeRef: CFTypeRef?
                var badge: String?
                if AXUIElementCopyAttributeValue(item, "AXStatusLabel" as CFString, &badgeRef) == .success {
                    badge = (badgeRef as? String) ?? (badgeRef as? NSNumber)?.stringValue ?? ""
                }
                out.append(Entry(title: AX.string(item, kAXTitleAttribute), url: url, badge: badge))
            }
        }
        return out
    }

    /// Badge for an app: matched by bundle URL, then by name. nil = no badge.
    static func badge(in entries: [Entry], bundleURL: URL?, name: String) -> String? {
        if let bundleURL {
            let want = bundleURL.standardizedFileURL.path
            if let e = entries.first(where: { $0.url?.standardizedFileURL.path == want }) { return e.badge }
        }
        return entries.first(where: { $0.title == name })?.badge
    }

    private static func children(_ el: AXUIElement) -> [AXUIElement]? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &v) == .success else { return nil }
        return v as? [AXUIElement]
    }
}

/// The Dock's "show hidden apps as translucent" option (com.apple.dock showhidden).
enum DockFade {
    static var isEnabled: Bool {
        CFPreferencesAppSynchronize("com.apple.dock" as CFString)
        return CFPreferencesCopyAppValue("showhidden" as CFString, "com.apple.dock" as CFString) as? Bool ?? false
    }

    /// Writes the Dock preference with `defaults` and restarts the Dock so it takes effect. Returns an error message.
    @discardableResult
    static func set(_ on: Bool) -> String? {
        let w = run("/usr/bin/defaults", ["write", "com.apple.dock", "showhidden", "-bool", on ? "YES" : "NO"])
        if w != 0 { return "defaults write failed (\(w))" }
        _ = run("/usr/bin/killall", ["Dock"])
        Log.info("Dock showhidden set to \(on); Dock restarted")
        return nil
    }

    private static func run(_ path: String, _ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run(); p.waitUntilExit(); return p.terminationStatus } catch { return -1 }
    }
}

enum BadgeImage {
    /// Short text for a badge: digits (capped at 99+); nil means "draw a dot".
    static func text(_ badge: String) -> String? {
        let t = badge.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty, let n = Int(t) else { return nil }
        return n > 99 ? "99+" : String(n)
    }

    /// The app icon with a small red badge in the top-right corner.
    @MainActor
    static func compose(icon: NSImage, badge: String?) -> NSImage {
        let iconSize: CGFloat = 18
        guard let badge else {
            let img = icon.copy() as! NSImage
            img.size = NSSize(width: iconSize, height: iconSize)
            return img
        }
        let label = text(badge)
        let font = NSFont.systemFont(ofSize: 8, weight: .bold)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.white]
        let textSize = label.map { ($0 as NSString).size(withAttributes: attrs) } ?? .zero
        let h: CGFloat = label == nil ? 7 : 10
        let w: CGFloat = label == nil ? 7 : max(h, ceil(textSize.width) + 4)
        let size = NSSize(width: iconSize + max(0, w - 6), height: iconSize)
        let img = NSImage(size: size)
        img.lockFocus()
        icon.draw(in: NSRect(x: 0, y: 0, width: iconSize, height: iconSize), from: .zero, operation: .sourceOver, fraction: 1)
        let rect = NSRect(x: size.width - w, y: size.height - h, width: w, height: h)
        NSColor.systemRed.setFill()
        NSBezierPath(roundedRect: rect, xRadius: h / 2, yRadius: h / 2).fill()
        if let label {
            (label as NSString).draw(at: NSPoint(x: rect.midX - textSize.width / 2, y: rect.midY - textSize.height / 2), withAttributes: attrs)
        }
        img.unlockFocus()
        return img
    }
}
