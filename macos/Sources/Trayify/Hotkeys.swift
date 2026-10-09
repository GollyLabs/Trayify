import AppKit
import Carbon

/// A key combination: Carbon modifier flags (cmdKey, optionKey, controlKey, shiftKey) + virtual key code.
struct Hotkey: Codable, Hashable, Sendable, CustomStringConvertible {
    var keyCode: UInt32
    var modifiers: UInt32

    static let cmd = UInt32(cmdKey), shift = UInt32(shiftKey), option = UInt32(optionKey), control = UInt32(controlKey)

    var description: String {
        var s = ""
        if modifiers & Hotkey.control != 0 { s += "⌃" }
        if modifiers & Hotkey.option != 0 { s += "⌥" }
        if modifiers & Hotkey.shift != 0 { s += "⇧" }
        if modifiers & Hotkey.cmd != 0 { s += "⌘" }
        return s + KeyNames.name(keyCode)
    }

    /// Text form used by the command channel, e.g. "ctrl+opt+n".
    var spec: String {
        var parts: [String] = []
        if modifiers & Hotkey.control != 0 { parts.append("ctrl") }
        if modifiers & Hotkey.option != 0 { parts.append("opt") }
        if modifiers & Hotkey.shift != 0 { parts.append("shift") }
        if modifiers & Hotkey.cmd != 0 { parts.append("cmd") }
        parts.append(KeyNames.name(keyCode).lowercased())
        return parts.joined(separator: "+")
    }

    static func carbonModifiers(_ flags: NSEvent.ModifierFlags) -> UInt32 {
        var m: UInt32 = 0
        if flags.contains(.command) { m |= cmd }
        if flags.contains(.option) { m |= option }
        if flags.contains(.control) { m |= control }
        if flags.contains(.shift) { m |= shift }
        return m
    }

    /// A shortcut needs a modifier, except F1–F20 which may stand alone.
    var isValid: Bool { modifiers != 0 || KeyNames.fKeys.contains(keyCode) }

    /// Parses "ctrl+opt+n", "cmd+shift+f5", "f13". Returns .success(nil) for "none".
    static func parse(_ text: String) -> Result<Hotkey?, HotkeyParseError> {
        let t = text.trimmingCharacters(in: .whitespaces).lowercased()
        if t.isEmpty || t == "none" { return .success(nil) }
        var mods: UInt32 = 0
        var key: UInt32?
        for raw in t.split(separator: "+").map({ $0.trimmingCharacters(in: .whitespaces) }) where !raw.isEmpty {
            switch raw {
            case "cmd", "command", "⌘": mods |= cmd
            case "opt", "option", "alt", "⌥": mods |= option
            case "ctrl", "control", "⌃": mods |= control
            case "shift", "⇧": mods |= shift
            default:
                if key != nil { return .failure(.init(message: "more than one key")) }
                guard let k = KeyNames.code(raw) else { return .failure(.init(message: "unknown key '\(raw)'")) }
                key = k
            }
        }
        guard let key else { return .failure(.init(message: "no key")) }
        let hk = Hotkey(keyCode: key, modifiers: mods)
        guard hk.isValid else { return .failure(.init(message: "needs a modifier (cmd, opt, ctrl, shift) or an F-key")) }
        return .success(hk)
    }
}

struct HotkeyParseError: Error { let message: String }

/// Names for macOS virtual key codes (ANSI layout).
enum KeyNames {
    static let table: [(UInt32, String)] = [
        (0x00, "A"), (0x0B, "B"), (0x08, "C"), (0x02, "D"), (0x0E, "E"), (0x03, "F"), (0x05, "G"), (0x04, "H"),
        (0x22, "I"), (0x26, "J"), (0x28, "K"), (0x25, "L"), (0x2E, "M"), (0x2D, "N"), (0x1F, "O"), (0x23, "P"),
        (0x0C, "Q"), (0x0F, "R"), (0x01, "S"), (0x11, "T"), (0x20, "U"), (0x09, "V"), (0x0D, "W"), (0x07, "X"),
        (0x10, "Y"), (0x06, "Z"),
        (0x1D, "0"), (0x12, "1"), (0x13, "2"), (0x14, "3"), (0x15, "4"), (0x17, "5"), (0x16, "6"), (0x1A, "7"),
        (0x1C, "8"), (0x19, "9"),
        (0x7A, "F1"), (0x78, "F2"), (0x63, "F3"), (0x76, "F4"), (0x60, "F5"), (0x61, "F6"), (0x62, "F7"),
        (0x64, "F8"), (0x65, "F9"), (0x6D, "F10"), (0x67, "F11"), (0x6F, "F12"), (0x69, "F13"), (0x6B, "F14"),
        (0x71, "F15"), (0x6A, "F16"), (0x40, "F17"), (0x4F, "F18"), (0x50, "F19"), (0x5A, "F20"),
        (0x31, "Space"), (0x24, "Return"), (0x30, "Tab"), (0x35, "Esc"), (0x33, "Delete"), (0x75, "FwdDelete"),
        (0x73, "Home"), (0x77, "End"), (0x74, "PageUp"), (0x79, "PageDown"),
        (0x7B, "Left"), (0x7C, "Right"), (0x7D, "Down"), (0x7E, "Up"),
        (0x1B, "-"), (0x18, "="), (0x21, "["), (0x1E, "]"), (0x29, ";"), (0x27, "'"), (0x2B, ","), (0x2F, "."),
        (0x2C, "/"), (0x2A, "\\"), (0x32, "`"),
        (0x52, "Num0"), (0x53, "Num1"), (0x54, "Num2"), (0x55, "Num3"), (0x56, "Num4"), (0x57, "Num5"),
        (0x58, "Num6"), (0x59, "Num7"), (0x5B, "Num8"), (0x5C, "Num9"),
    ]

    static let fKeys: Set<UInt32> = [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D,
                                     0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A]

    static func name(_ code: UInt32) -> String {
        table.first(where: { $0.0 == code })?.1 ?? String(format: "Key%02X", code)
    }

    static func code(_ name: String) -> UInt32? {
        table.first(where: { $0.1.lowercased() == name.lowercased() })?.0
    }
}

/// Registers the per-rule global shortcuts with Carbon RegisterEventHotKey (system-wide, no extra permission).
@MainActor
final class HotkeyManager {
    private var refs: [UInt32: EventHotKeyRef] = [:]
    private var idToBundle: [UInt32: String] = [:]
    private var down: Set<UInt32> = []
    private(set) var errors: [String: String] = [:]
    private var rules: [AppRule] = []
    private(set) var suspended = false
    private var handlerRef: EventHandlerRef?

    var onPressed: ((String) -> Void)?
    var onStatusChanged: (() -> Void)?

    init() {
        var specs = [
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased)),
        ]
        let me = Unmanaged.passUnretained(self).toOpaque()
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, userData in
            guard let event, let userData else { return OSStatus(eventNotHandledErr) }
            var hk = EventHotKeyID()
            let err = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                        nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            guard err == noErr else { return err }
            let pressed = GetEventKind(event) == UInt32(kEventHotKeyPressed)
            let id = hk.id
            let mgr = Unmanaged<HotkeyManager>.fromOpaque(userData).takeUnretainedValue()
            MainActor.assumeIsolated { mgr.handle(id: id, pressed: pressed) }
            return noErr
        }, specs.count, &specs, me, &handlerRef)
        if status != noErr { Log.error("InstallEventHandler failed: \(status)") }
    }

    private func handle(id: UInt32, pressed: Bool) {
        if !pressed { down.remove(id); return }
        if down.contains(id) { return } // key repeat
        down.insert(id)
        if let bid = idToBundle[id] { onPressed?(bid) }
    }

    func error(for bundleId: String) -> String? { errors[bundleId] }

    func apply(_ rules: [AppRule]) {
        self.rules = rules
        if !suspended { registerAll() }
    }

    /// Releases all shortcuts while the recorder captures a new one.
    func suspend() { suspended = true; unregisterAll(); onStatusChanged?() }
    func resume() { suspended = false; registerAll() }

    private func unregisterAll() {
        for ref in refs.values { UnregisterEventHotKey(ref) }
        refs.removeAll()
        idToBundle.removeAll()
        down.removeAll()
    }

    private func registerAll() {
        unregisterAll()
        errors.removeAll()
        var seen: [Hotkey: String] = [:]
        var next: UInt32 = 1
        for r in rules {
            guard let hk = r.hotkey else { continue }
            defer { next += 1 }
            if let other = seen[hk] {
                errors[r.bundleId] = "\(hk) is already used by \(other). Pick a different shortcut."
                continue
            }
            var ref: EventHotKeyRef?
            let st = RegisterEventHotKey(hk.keyCode, hk.modifiers, EventHotKeyID(signature: 0x5452_4659 /* TRFY */, id: next),
                                         GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &ref)
            if st == noErr, let ref {
                refs[next] = ref
                idToBundle[next] = r.bundleId
                seen[hk] = r.displayName
                Log.info("Hotkey \(hk.spec) registered for \(r.bundleId)")
            } else {
                errors[r.bundleId] = st == OSStatus(eventHotKeyExistsErr)
                    ? "\(hk) is already in use by another app. Pick a different shortcut."
                    : "Couldn't register \(hk) (error \(st))."
                Log.warn("Hotkey \(hk.spec) for \(r.bundleId) failed: \(st)")
            }
        }
        onStatusChanged?()
    }

    func describe() -> String {
        rules.compactMap { r -> String? in
            guard let hk = r.hotkey else { return nil }
            let state = errors[r.bundleId].map { "FAILED: " + $0 } ?? (suspended ? "suspended" : "registered")
            return "\(r.bundleId)\t\(hk.spec)\t\(state)"
        }.joined(separator: "\n")
    }
}
