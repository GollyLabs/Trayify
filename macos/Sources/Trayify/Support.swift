import Foundation

/// Locations of Trayify's files: ~/Library/Application Support/Trayify/.
enum Paths {
    static let dataDir: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Trayify", isDirectory: true)
    }()
    static var settingsFile: URL { dataDir.appendingPathComponent("settings.json") }
    static var hiddenFile: URL { dataDir.appendingPathComponent("hidden.json") }
    static var logFile: URL { dataDir.appendingPathComponent("trayify.log") }
    static var socketPath: String { dataDir.appendingPathComponent("trayify.sock").path }

    static func ensureDir() {
        let fm = FileManager.default
        if !fm.fileExists(atPath: dataDir.path) {
            try? fm.createDirectory(at: dataDir, withIntermediateDirectories: true,
                                    attributes: [.posixPermissions: 0o700])
        }
    }

    /// Writes data atomically (temp file + rename) with user-only permissions.
    static func writeAtomically(_ data: Data, to url: URL) throws {
        ensureDir()
        try data.write(to: url, options: [.atomic])
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}

/// Tiny thread-safe file logger (trayify.log, rotated at 2 MB).
enum Log {
    nonisolated(unsafe) static var tag = "app"
    private static let lock = NSLock()
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()

    static func info(_ m: String) { write("INFO", m) }
    static func warn(_ m: String) { write("WARN", m) }
    static func error(_ m: String) { write("ERROR", m) }

    private static func write(_ level: String, _ msg: String) {
        lock.lock(); defer { lock.unlock() }
        Paths.ensureDir()
        let line = "\(formatter.string(from: Date())) [\(tag) \(getpid())] \(level) \(msg)\n"
        let url = Paths.logFile
        let fm = FileManager.default
        if let attrs = try? fm.attributesOfItem(atPath: url.path),
           let size = attrs[.size] as? NSNumber, size.intValue > 2_000_000 {
            let old = url.appendingPathExtension("old")
            try? fm.removeItem(at: old)
            try? fm.moveItem(at: url, to: old)
        }
        guard let data = line.data(using: .utf8) else { return }
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile()
            h.write(data)
            try? h.close()
        } else {
            try? data.write(to: url)
            try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }
    }
}
