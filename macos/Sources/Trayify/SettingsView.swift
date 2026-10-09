import AppKit
import SwiftUI

struct SettingsView: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable().frame(width: 52, height: 52)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Trayify").font(.largeTitle.weight(.semibold))
                        Text("Send apps to the menu bar instead of closing or minimizing them.")
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 4)
            }

            if !model.axTrusted {
                Section { accessibilityBanner }
            }

            Section("General") {
                ToggleRow(icon: "minus.square", title: "Right-click minimize sends to menu bar",
                          caption: "Right-click the yellow minimize button of any window to send that app to the menu bar.",
                          isOn: Binding(get: { model.rightClickMinimize }, set: { model.setRightClick($0) }))
                ToggleRow(icon: "command", title: "⌘W also sends close-to-menu-bar apps to the menu bar",
                          caption: "Only for the apps listed below, and only when ⌘W would close the app's last window.",
                          isOn: Binding(get: { model.cmdW }, set: { model.setCmdW($0) }))
                ToggleRow(icon: "app.badge", title: "Show unread badges on menu bar icons",
                          caption: "Shows a hidden app's Dock badge (like an unread count) on its menu bar icon. Needs Accessibility.",
                          isOn: Binding(get: { model.showBadges }, set: { model.setShowBadges($0) }))
                ToggleRow(icon: "dock.rectangle", title: "Fade hidden apps in the Dock",
                          caption: "Shows hidden apps' Dock icons dimmed. Changes a Dock setting for all hidden apps and briefly restarts the Dock.",
                          isOn: Binding(get: { model.fadeDock }, set: { model.setFadeDock($0) }))
                ToggleRow(icon: "power", title: "Start at login",
                          caption: model.loginNote ?? "Opens Trayify quietly in the menu bar when you log in.",
                          isOn: Binding(get: { model.startAtLogin }, set: { model.setStartAtLogin($0) }))
            }

            Section {
                if model.rules.isEmpty {
                    Text("No apps yet. Turn on “Close to menu bar” for an app under Running apps below; its close button will then send it to the menu bar.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                ForEach(model.rules) { rule in RuleRowView(model: model, rule: rule) }
            } header: {
                Text("Close-to-menu-bar apps")
            }

            Section {
                if model.hidden.isEmpty {
                    Text("Nothing is hidden right now.").font(.callout).foregroundStyle(.secondary)
                }
                ForEach(model.hidden) { item in
                    HStack(spacing: 12) {
                        Image(nsImage: item.icon).resizable().frame(width: 28, height: 28)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.name).lineLimit(1)
                            Text(item.details).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer()
                        Button("Restore") { model.restore(item.pid) }
                            .buttonStyle(.borderedProminent)
                            .accessibilityLabel("Restore \(item.name)")
                    }
                }
            } header: {
                HStack {
                    Text(model.hidden.isEmpty ? "In the menu bar" : "In the menu bar (\(model.hidden.count))")
                    Spacer()
                    Button("Restore All") { model.restoreAll() }.disabled(model.hidden.isEmpty)
                }
            }

            Section {
                ForEach(model.running) { item in
                    HStack(spacing: 12) {
                        Image(nsImage: item.icon).resizable().frame(width: 28, height: 28)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.name).lineLimit(1)
                            Text(item.bundleId).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer()
                        Button { model.sendNow(item) } label: { Image(systemName: "menubar.arrow.up.rectangle") }
                            .help("Send this app to the menu bar now")
                            .accessibilityLabel("Send \(item.name) to the menu bar now")
                        Toggle("Close to menu bar", isOn: Binding(
                            get: { model.ruleIds.contains(item.bundleId) },
                            set: { model.setRule(item, $0) }))
                            .toggleStyle(.switch)
                            .help("Its close button sends it to the menu bar instead of closing")
                    }
                }
            } header: {
                HStack {
                    Text("Running apps")
                    Spacer()
                    Button { model.refreshRunning_() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
                }
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 600, minHeight: 500)
    }

    private var accessibilityBanner: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.title2)
            VStack(alignment: .leading, spacing: 6) {
                Text("Accessibility access needed").font(.headline)
                Text("Trayify needs Accessibility permission to notice clicks on close and minimize buttons and ⌘W. Shortcuts, the menu bar items and “Send to menu bar now” work without it.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("Grant Access…") { model.requestAccessibility() }.buttonStyle(.borderedProminent)
                    Button("Open System Settings") { model.openAccessibilitySettings() }
                }
            }
        }
        .padding(.vertical, 4)
    }
}

struct ToggleRow: View {
    let icon: String
    let title: String
    let caption: String
    @Binding var isOn: Bool

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon).font(.title3).frame(width: 24)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(caption).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Toggle(title, isOn: $isOn).labelsHidden().toggleStyle(.switch)
        }
    }
}

struct RuleRowView: View {
    @ObservedObject var model: SettingsModel
    let rule: RuleRow

    private var isRecording: Bool { model.recording == rule.bundleId }

    var body: some View {
        HStack(spacing: 12) {
            Image(nsImage: rule.icon).resizable().frame(width: 28, height: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(rule.name).lineLimit(1)
                Text(rule.bundleId).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                if isRecording {
                    Text(model.recorderHint).font(.caption).foregroundStyle(.secondary)
                } else if let err = rule.error {
                    Text(err).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer()
            Button {
                if isRecording { model.stopRecording() } else { model.startRecording(rule.bundleId) }
            } label: {
                Label(isRecording ? "Type shortcut…" : (rule.hotkey?.description ?? "Add shortcut"), systemImage: "keyboard")
                    .frame(minWidth: 110)
            }
            .help("Global shortcut: sends this app to the menu bar, or restores it")
            .accessibilityLabel("Shortcut for \(rule.name)")
            if rule.hotkey != nil {
                Button { model.clearHotkey(rule.bundleId) } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless)
                    .help("Clear shortcut")
                    .accessibilityLabel("Clear shortcut for \(rule.name)")
            }
            Button("Remove") { model.removeRule(rule.bundleId) }
                .accessibilityLabel("Remove \(rule.name)")
        }
    }
}

/// Hosts the SwiftUI settings view. Closing the window only hides it; Trayify keeps running.
@MainActor
final class SettingsWindowController: NSWindowController, NSWindowDelegate {
    private let model: SettingsModel

    init(model: SettingsModel) {
        self.model = model
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: 860),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "Trayify"
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: SettingsView(model: model))
        window.setContentSize(NSSize(width: 760, height: 860))
        window.center()
        window.setFrameAutosaveName("TrayifySettings")
        super.init(window: window)
        window.delegate = self
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    var isVisible: Bool { window?.isVisible ?? false }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        window?.orderFrontRegardless()
    }

    func windowDidResignKey(_ notification: Notification) { model.stopRecording() }
    func windowWillClose(_ notification: Notification) { model.stopRecording() }
}
