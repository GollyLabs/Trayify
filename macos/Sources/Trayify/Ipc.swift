import Foundation

/// Local control channel: a Unix domain socket in the user-only data folder
/// (~/Library/Application Support/Trayify/trayify.sock, mode 0600, peer uid checked).
/// Protocol: client sends one line, server replies with text and closes.
enum IpcSocket {
    static func address(_ path: String) -> sockaddr_un {
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        addr.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        let bytes = Array(path.utf8.prefix(MemoryLayout.size(ofValue: addr.sun_path) - 1))
        withUnsafeMutableBytes(of: &addr.sun_path) { raw in
            raw.copyBytes(from: bytes)
            raw[bytes.count] = 0
        }
        return addr
    }

    static func readAll(_ fd: Int32, untilNewline: Bool, limit: Int = 1 << 20) -> String {
        var data = Data()
        var buf = [UInt8](repeating: 0, count: 4096)
        while data.count < limit {
            let n = read(fd, &buf, buf.count)
            if n <= 0 { break }
            data.append(buf, count: n)
            if untilNewline, buf[0..<n].contains(10) { break }
        }
        return String(decoding: data, as: UTF8.self)
    }

    static func writeAll(_ fd: Int32, _ s: String) {
        let bytes = Array(s.utf8)
        var off = 0
        while off < bytes.count {
            let n = bytes[off...].withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
            if n <= 0 { break }
            off += n
        }
    }
}

final class IpcServer: @unchecked Sendable {
    private let handler: @Sendable (String) -> String
    private var fd: Int32 = -1
    private var closed = false

    init(handler: @escaping @Sendable (String) -> String) { self.handler = handler }

    func start() {
        Paths.ensureDir()
        let path = Paths.socketPath
        unlink(path)
        fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { Log.error("socket() failed: \(errno)"); return }
        var addr = IpcSocket.address(path)
        let old = umask(0o077)
        let rc = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        umask(old)
        guard rc == 0 else { Log.error("bind(\(path)) failed: \(errno)"); close(fd); fd = -1; return }
        chmod(path, 0o600)
        guard listen(fd, 8) == 0 else { Log.error("listen failed: \(errno)"); return }
        let listenFd = fd
        let thread = Thread { [weak self] in
            while true {
                let c = accept(listenFd, nil, nil)
                guard let self else { if c >= 0 { close(c) }; return }
                if c < 0 { if self.closed { return }; continue }
                self.serve(c)
            }
        }
        thread.name = "Trayify.Ipc"
        thread.start()
        Log.info("Control socket listening at \(path)")
    }

    private func serve(_ c: Int32) {
        defer { close(c) }
        var uid: uid_t = 0, gid: gid_t = 0
        guard getpeereid(c, &uid, &gid) == 0, uid == getuid() else { Log.warn("Rejected control connection from uid \(uid)"); return }
        var tv = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        let line = IpcSocket.readAll(c, untilNewline: true, limit: 64 * 1024)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !line.isEmpty else { return }
        let reply = handler(line)
        IpcSocket.writeAll(c, reply.hasSuffix("\n") ? reply : reply + "\n")
    }

    func stop() {
        closed = true
        if fd >= 0 { close(fd); fd = -1 }
        unlink(Paths.socketPath)
    }
}

enum IpcClient {
    /// Sends a command to the running Trayify; nil if it isn't running.
    static func send(_ command: String, timeout: Int = 10) -> String? {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        var addr = IpcSocket.address(Paths.socketPath)
        let rc = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        guard rc == 0 else { return nil }
        var tv = timeval(tv_sec: timeout, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        IpcSocket.writeAll(fd, command + "\n")
        shutdown(fd, SHUT_WR)
        return IpcSocket.readAll(fd, untilNewline: false).trimmingCharacters(in: .newlines)
    }
}
