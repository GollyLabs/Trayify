#if DEBUG
import CoreGraphics
import Foundation

/// Synthetic input for end-to-end testing of the event tap (DEBUG builds only).
/// test-input left|right <x> <y>   |   test-input cmdw   |   test-input stall <seconds>
enum TestInput {
    nonisolated(unsafe) static var stallSeconds: Double = 0

    static func run(_ arg: String) -> String {
        let a = arg.split(separator: " ").map(String.init)
        guard let kind = a.first else { return "usage: test-input left|right <x> <y> | cmdw | stall <s>" }
        let src = CGEventSource(stateID: .hidSystemState)
        func post(_ e: CGEvent?) { e?.post(tap: .cghidEventTap); usleep(50_000) }
        switch kind {
        case "cmdw":
            let d = CGEvent(keyboardEventSource: src, virtualKey: 0x0D, keyDown: true); d?.flags = .maskCommand
            let u = CGEvent(keyboardEventSource: src, virtualKey: 0x0D, keyDown: false); u?.flags = .maskCommand
            post(d); post(u)
        case "position":
            // Simulates the user ⌘-dragging a status item: AppKit stores the result in our own defaults.
            guard a.count == 3, let v = Double(a[2]) else { return "usage: test-input position <autosaveName> <value>" }
            UserDefaults.standard.set(v, forKey: "NSStatusItem Preferred Position \(a[1])")
            return "set"
        case "stall":
            stallSeconds = a.count > 1 ? Double(a[1]) ?? 0 : 0
            return "next intercepted mouse-down stalls the tap for \(stallSeconds)s"
        case "left", "right":
            guard a.count == 3, let x = Double(a[1]), let y = Double(a[2]) else { return "usage: test-input left|right <x> <y>" }
            let p = CGPoint(x: x, y: y)
            let right = kind == "right"
            let btn: CGMouseButton = right ? .right : .left
            post(CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left))
            usleep(100_000)
            post(CGEvent(mouseEventSource: src, mouseType: right ? .rightMouseDown : .leftMouseDown, mouseCursorPosition: p, mouseButton: btn))
            post(CGEvent(mouseEventSource: src, mouseType: right ? .rightMouseUp : .leftMouseUp, mouseCursorPosition: p, mouseButton: btn))
        default:
            return "unknown test-input"
        }
        return "posted"
    }
}
#endif
