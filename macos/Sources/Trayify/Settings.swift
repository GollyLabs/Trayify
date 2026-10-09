import Foundation

/// A close-to-menu-bar rule, keyed by bundle identifier.
struct AppRule: Codable, Equatable, Identifiable, Sendable {
    var bundleId: String
    var name: String?
    var path: String?
    var added: Date = Date()
    var hotkey: Hotkey?

    var id: String { bundleId }
    var displayName: String { name ?? bundleId }

    init(bundleId: String, name: String?, path: String?, hotkey: Hotkey? = nil) {
        self.bundleId = bundleId
        self.name = name
        self.path = path
        self.hotkey = hotkey
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        bundleId = try c.decode(String.self, forKey: .bundleId)
        name = try c.decodeIfPresent(String.self, forKey: .name)
        path = try c.decodeIfPresent(String.self, forKey: .path)
        added = try c.decodeIfPresent(Date.self, forKey: .added) ?? Date()
        hotkey = try c.decodeIfPresent(Hotkey.self, forKey: .hotkey)
    }
}

struct AppSettings: Codable, Sendable {
    var rules: [AppRule] = []
    /// Right-click on any window's minimize button sends its app to the menu bar.
    var rightClickMinimize = true
    /// Cmd+W on a close-to-menu-bar app's last window sends it to the menu bar.
    var cmdWToMenuBar = true

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        rules = try c.decodeIfPresent([AppRule].self, forKey: .rules) ?? []
        rightClickMinimize = try c.decodeIfPresent(Bool.self, forKey: .rightClickMinimize) ?? true
        cmdWToMenuBar = try c.decodeIfPresent(Bool.self, forKey: .cmdWToMenuBar) ?? true
    }
}

enum SettingsStore {
    private static func encoder() -> JSONEncoder {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        e.dateEncodingStrategy = .iso8601
        return e
    }

    private static func decoder() -> JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }

    static func load() -> AppSettings {
        do {
            guard FileManager.default.fileExists(atPath: Paths.settingsFile.path) else { return AppSettings() }
            let data = try Data(contentsOf: Paths.settingsFile)
            return try decoder().decode(AppSettings.self, from: data)
        } catch {
            Log.error("Failed to load settings: \(error)")
            return AppSettings()
        }
    }

    static func save(_ s: AppSettings) {
        do { try Paths.writeAtomically(try encoder().encode(s), to: Paths.settingsFile) }
        catch { Log.error("Failed to save settings: \(error)") }
    }
}
