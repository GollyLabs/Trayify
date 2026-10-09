import AppKit
import ApplicationServices

enum CaptionButton: String { case close, minimize, none }

/// Accessibility helpers (all safe to call from any thread).
enum AX {
    static func string(_ el: AXUIElement, _ attr: String) -> String? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, attr as CFString, &v) == .success else { return nil }
        return v as? String
    }

    static func element(_ el: AXUIElement, _ attr: String) -> AXUIElement? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, attr as CFString, &v) == .success, let v,
              CFGetTypeID(v) == AXUIElementGetTypeID() else { return nil }
        return (v as! AXUIElement)
    }

    static func pid(_ el: AXUIElement) -> pid_t? {
        var p: pid_t = 0
        return AXUIElementGetPid(el, &p) == .success ? p : nil
    }

    /// What window-control button (if any) is at a screen point (top-left origin, as in CGEvent.location).
    static func classify(_ point: CGPoint, systemWide: AXUIElement) -> (pid: pid_t, button: CaptionButton)? {
        var hit: AXUIElement?
        guard AXUIElementCopyElementAtPosition(systemWide, Float(point.x), Float(point.y), &hit) == .success,
              let hit, let pid = pid(hit) else { return nil }
        var cur: AXUIElement? = hit
        for _ in 0..<2 {
            guard let c = cur else { break }
            switch string(c, kAXSubroleAttribute) {
            case kAXCloseButtonSubrole: return (pid, .close)
            case kAXMinimizeButtonSubrole: return (pid, .minimize)
            default: break
            }
            cur = element(c, kAXParentAttribute)
        }
        return (pid, .none)
    }

    /// Number of standard windows (including minimized ones) an app has; nil if unknown.
    static func standardWindowCount(pid: pid_t) -> Int? {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.3)
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &v) == .success,
              let wins = v as? [AXUIElement] else { return nil }
        return wins.filter { string($0, kAXSubroleAttribute) == kAXStandardWindowSubrole }.count
    }
}

/// Global CGEventTap (needs Accessibility permission) that catches:
///  - left click on the close button of an app with a rule      -> hide the app instead
///  - right click on the minimize button of any app (if enabled) -> hide the app
///  - Cmd+W on a ruled app with exactly one standard window (if enabled) -> hide the app
/// The tap runs on its own thread so slow Accessibility queries never block the UI.
final class InputInterceptor: @unchecked Sendable {
    private let lock = NSLock()
    private var rules = Set<String>()
    private var rightClickMinimize = true
    private var cmdW = true
    private var frontmostPid: pid_t = 0
    private var bundleCache: [pid_t: String] = [:]
    private var tap: CFMachPort?
    private var runLoop: CFRunLoop?
    private(set) var intercepts = 0

    // Tap-thread-only state.
    private var swallowLeftUp = false
    private var swallowRightUp = false
    private let systemWide: AXUIElement = {
        let e = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(e, 0.3)
        return e
    }()

    private let onHide: @Sendable (pid_t, HideReason) -> Void
    private let selfPid = getpid()

    init(onHide: @escaping @Sendable (pid_t, HideReason) -> Void) {
        self.onHide = onHide
    }

    private func locked<T>(_ f: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return f() }

    func configure(rules: Set<String>, rightClickMinimize: Bool, cmdW: Bool) {
        locked {
            self.rules = rules
            self.rightClickMinimize = rightClickMinimize
            self.cmdW = cmdW
        }
    }

    func setFrontmost(_ pid: pid_t) { locked { frontmostPid = pid } }
    func forget(pid: pid_t) { _ = locked { bundleCache.removeValue(forKey: pid) } }

    var isRunning: Bool { locked { tap != nil } }
    var interceptCount: Int { locked { intercepts } }

    /// Creates the tap; returns false if macOS refuses (no Accessibility permission yet).
    @discardableResult
    func start() -> Bool {
        if isRunning { return true }
        let types: [CGEventType] = [.leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .keyDown]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << CGEventMask($1.rawValue)) }
        let callback: CGEventTapCallBack = { _, type, event, refcon in
            guard let refcon else { return Unmanaged.passUnretained(event) }
            let me = Unmanaged<InputInterceptor>.fromOpaque(refcon).takeUnretainedValue()
            return me.handle(type, event)
        }
        guard let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                           eventsOfInterest: mask, callback: callback,
                                           userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
            return false
        }
        locked { tap = port }
        nonisolated(unsafe) let source = CFMachPortCreateRunLoopSource(nil, port, 0)
        nonisolated(unsafe) let tapPort = port
        let thread = Thread { [weak self] in
            let rl = CFRunLoopGetCurrent()
            CFRunLoopAddSource(rl, source, .commonModes)
            CGEvent.tapEnable(tap: tapPort, enable: true)
            self?.locked { self?.runLoop = rl }
            CFRunLoopRun()
        }
        thread.name = "Trayify.EventTap"
        thread.qualityOfService = .userInteractive
        thread.start()
        Log.info("Event tap installed")
        return true
    }

    func stop() {
        let (t, rl) = locked { () -> (CFMachPort?, CFRunLoop?) in
            let r = (tap, runLoop)
            tap = nil; runLoop = nil
            return r
        }
        if let t { CGEvent.tapEnable(tap: t, enable: false); CFMachPortInvalidate(t) }
        if let rl { CFRunLoopStop(rl) }
    }

    private func bundleId(_ pid: pid_t) -> String? {
        if let b = locked({ bundleCache[pid] }) { return b }
        guard let b = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier else { return nil }
        locked { bundleCache[pid] = b }
        return b
    }

    private func intercepted(_ pid: pid_t, _ reason: HideReason) {
        locked { intercepts += 1 }
        onHide(pid, reason)
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        let pass = Unmanaged.passUnretained(event)
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            if let t = locked({ tap }) { CGEvent.tapEnable(tap: t, enable: true) }
            Log.warn("Event tap was disabled (\(type.rawValue)); re-enabled")
            return pass

        case .leftMouseDown:
            let ruleSet = locked { rules }
            guard !ruleSet.isEmpty,
                  let (pid, button) = AX.classify(event.location, systemWide: systemWide),
                  button == .close, pid != selfPid,
                  let bid = bundleId(pid), ruleSet.contains(bid) else { return pass }
            swallowLeftUp = true
            intercepted(pid, .closeButton)
            return nil

        case .leftMouseUp:
            if swallowLeftUp { swallowLeftUp = false; return nil }
            return pass

        case .rightMouseDown:
            guard locked({ rightClickMinimize }),
                  let (pid, button) = AX.classify(event.location, systemWide: systemWide),
                  button == .minimize, pid != selfPid else { return pass }
            swallowRightUp = true
            intercepted(pid, .minimizeRightClick)
            return nil

        case .rightMouseUp:
            if swallowRightUp { swallowRightUp = false; return nil }
            return pass

        case .keyDown:
            let (enabled, ruleSet, front) = locked { (cmdW, rules, frontmostPid) }
            guard enabled, !ruleSet.isEmpty,
                  event.getIntegerValueField(.keyboardEventKeycode) == 0x0D, // W
                  event.getIntegerValueField(.keyboardEventAutorepeat) == 0 else { return pass }
            let flags = event.flags.intersection([.maskCommand, .maskShift, .maskAlternate, .maskControl])
            guard flags == .maskCommand else { return pass }
            var pid = front
            if pid <= 0 { pid = pid_t(truncatingIfNeeded: event.getIntegerValueField(.eventTargetUnixProcessID)) }
            guard pid > 0, pid != selfPid, let bid = bundleId(pid), ruleSet.contains(bid),
                  AX.standardWindowCount(pid: pid) == 1 else { return pass }
            intercepted(pid, .cmdW)
            return nil

        default:
            return pass
        }
    }
}
