import Foundation
import Capacitor
import Network
import Security

/// Read-only SMB access for the app's Files page on iOS: the same methods,
/// arguments, results and error codes as the Android plugin
/// (PolarisSmbPlugin.java), so files/network.js runs unchanged.
///
/// The SMB protocol comes from SMBClient by Kishikawa Katsumi (MIT), vendored
/// in ./SMBClient at a pinned commit (see SMBClient/VENDORED.md).
///
/// Passwords live in the Keychain, readable only on this device, under the
/// server's id; the page never reads them back.
@objc(PolarisSmbPlugin)
public class PolarisSmbPlugin: CAPPlugin {

    private let pool = ClientPool()
    private let downloads = DownloadFlags()
    private let cacheDir: URL = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("smb", isDirectory: true)

    override public func load() {
        // Files from an earlier run that the page never released.
        try? FileManager.default.removeItem(at: cacheDir)
        try? FileManager.default.createDirectory(at: cacheDir, withIntermediateDirectories: true)
    }

    private func run(_ call: CAPPluginCall, _ work: @escaping () async throws -> Void) {
        Task.detached {
            do { try await work() } catch { call.reject(Self.message(error), Self.code(error)) }
        }
    }

    // MARK: - discovery

    @objc func discover(_ call: CAPPluginCall) {
        let timeout = Double(call.getInt("timeoutMs") ?? 1500) / 1000
        run(call) {
            let onNetwork = NetBios.localIPv4() != nil
            let found = onNetwork ? NetBios.sweep(timeout: timeout) : []
            let servers = found.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
                .map { ["name": $0.name, "address": $0.address] }
            call.resolve(["servers": servers, "onNetwork": onNetwork])
        }
    }

    // MARK: - credentials

    @objc func hasCredentials(_ call: CAPPluginCall) {
        let id = call.getString("server") ?? ""
        let c = Keychain.load(server: id)
        call.resolve(["saved": c != nil, "username": c?.username ?? ""])
    }

    @objc func forgetCredentials(_ call: CAPPluginCall) {
        let id = call.getString("server") ?? ""
        Keychain.forget(server: id)
        Task { await pool.dropAll(); call.resolve() }
    }

    // MARK: - browse

    @objc func listShares(_ call: CAPPluginCall) {
        run(call) {
            try await self.withClient(call) { box, address in
                let shares = try await box.run { client in try await client.listShares() }
                let disk = shares
                    .filter { $0.type == .diskTree && !$0.name.hasSuffix("$") }
                    .map { ["name": $0.name, "comment": $0.comment] }
                call.resolve(["address": address, "name": self.serverName(call, address), "shares": disk])
            }
        }
    }

    @objc func list(_ call: CAPPluginCall) {
        let share = call.getString("share") ?? ""
        let path = Self.smbPath(call.getString("path") ?? "")
        run(call) {
            try await self.withClient(call) { box, address in
                let files = try await box.run { client in
                    try await box.tree(share, client).listDirectory(path: path)
                }
                var dirs: [[String: Any]] = [], plain: [[String: Any]] = []
                for f in files where f.name != "." && f.name != ".." && !f.isHidden {
                    let o: [String: Any] = [
                        "name": f.name,
                        "dir": f.isDirectory,
                        "size": f.isDirectory ? 0 : Double(f.size),
                        "modified": (f.lastWriteTime.timeIntervalSince1970 * 1000).rounded(),
                    ]
                    if f.isDirectory { dirs.append(o) } else { plain.append(o) }
                }
                call.resolve(["address": address, "entries": dirs + plain])
            }
        }
    }

    @objc func download(_ call: CAPPluginCall) {
        let id = call.getString("id") ?? ""
        let share = call.getString("share") ?? ""
        let path = Self.smbPath(call.getString("path") ?? "")
        run(call) {
            await self.downloads.start(id)
            defer { Task { await self.downloads.finish(id) } }
            try await self.withClient(call) { box, address in
                let leaf = (path.split(separator: "\\").last.map(String.init) ?? "file")
                    .replacingOccurrences(of: "[^A-Za-z0-9._-]", with: "_", options: .regularExpression)
                let out = self.cacheDir.appendingPathComponent(UUID().uuidString + "-" + leaf)
                FileManager.default.createFile(atPath: out.path, contents: nil)
                var ok = false
                defer { if !ok { try? FileManager.default.removeItem(at: out) } }
                let handle = try FileHandle(forWritingTo: out)
                defer { try? handle.close() }

                let received: UInt64 = try await box.run { client in
                    let reader = try await box.tree(share, client).fileReader(path: path)
                    do {
                        let total = try await reader.fileSize
                        var offset: UInt64 = 0
                        var lastNote = Date.distantPast
                        while offset < total {
                            if await self.downloads.isCancelled(id) { throw DownloadCancelled() }
                            let chunk = try await reader.read(offset: offset)
                            if chunk.isEmpty { break }
                            try handle.write(contentsOf: chunk)
                            offset += UInt64(chunk.count)
                            if Date().timeIntervalSince(lastNote) > 0.2 {
                                lastNote = Date()
                                self.notifyListeners("downloadProgress", data: [
                                    "id": id, "received": Double(offset), "total": Double(total),
                                ])
                            }
                        }
                        // Closed here, inside the client's turn, so that it doesn't
                        // overlap the next operation on the same connection.
                        try? await reader.close()
                        return offset
                    } catch {
                        try? await reader.close()
                        throw error
                    }
                }
                ok = true
                call.resolve(["address": address, "path": out.path, "size": Double(received)])
            }
        }
    }

    @objc func cancelDownload(_ call: CAPPluginCall) {
        let id = call.getString("id") ?? ""
        Task { await downloads.cancel(id); call.resolve() }
    }

    /// Delete a file download() wrote. Only files in the plugin's cache folder.
    @objc func releaseFile(_ call: CAPPluginCall) {
        let url = URL(fileURLWithPath: call.getString("path") ?? "").standardizedFileURL
        if url.deletingLastPathComponent().path == cacheDir.standardizedFileURL.path {
            try? FileManager.default.removeItem(at: url)
        }
        call.resolve()
    }

    // MARK: - open with

    /// Name and size of a file:// URL that "Open with" handed the app.
    @objc func describeUri(_ call: CAPPluginCall) {
        guard let s = call.getString("uri"), let url = URL(string: s) else {
            call.resolve(["name": "", "size": -1]); return
        }
        var size: Double = -1
        if url.isFileURL, let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
           let n = attrs[.size] as? NSNumber {
            size = n.doubleValue
        }
        call.resolve(["name": url.lastPathComponent.removingPercentEncoding ?? url.lastPathComponent, "size": size])
    }

    // MARK: - sessions

    /// Run work with a signed-in client for the call's server. When the server
    /// doesn't answer and its NetBIOS name is known, look the name up again
    /// once, because DHCP may have moved it to another address.
    private func withClient(_ call: CAPPluginCall, _ work: (ClientBox, String) async throws -> Void) async throws {
        let host = (call.getString("host") ?? "").trimmingCharacters(in: .whitespaces)
        let name = call.getString("name") ?? ""
        let server = call.getString("server") ?? host
        let creds = try credentials(call, server: server)

        var address = host
        if !Self.isIPv4(host) && !host.contains(".") {
            // A bare name such as JUPITER: iOS doesn't resolve NetBIOS names.
            guard let a = NetBios.find(name: host, timeout: 1.5) else { throw Unreachable(host: host) }
            address = a
        }
        do {
            try await work(try await pool.client(address, creds), address)
        } catch {
            guard Self.isUnreachable(error), !name.isEmpty else {
                if !(error is ErrorResponse) { await pool.drop(address) }
                throw error
            }
            await pool.drop(address)
            guard let a = NetBios.find(name: name, timeout: 1.5), a != address else { throw error }
            address = a
            try await work(try await pool.client(address, creds), address)
        }
    }

    private func credentials(_ call: CAPPluginCall, server: String) throws -> Creds? {
        if let user = call.getString("username") {
            let c = Creds(username: user, password: call.getString("password") ?? "", domain: call.getString("domain") ?? "")
            if call.getBool("remember") ?? false { try Keychain.save(server: server, c) }
            return c
        }
        return Keychain.load(server: server)
    }

    private func serverName(_ call: CAPPluginCall, _ address: String) -> String {
        if let n = call.getString("name"), !n.isEmpty { return n }
        let host = call.getString("host") ?? ""
        if !Self.isIPv4(host) && !host.contains(".") { return host.uppercased() }
        return NetBios.nameOf(address: address, timeout: 0.8) ?? ""
    }

    // MARK: - errors

    struct DownloadCancelled: Error {}
    struct Unreachable: LocalizedError {
        let host: String
        var errorDescription: String? { "No machine called \(host) answered on this network." }
    }
    struct Timeout: LocalizedError {
        var errorDescription: String? { "The server didn't answer in time." }
    }

    static func isIPv4(_ s: String) -> Bool {
        s.range(of: #"^\d{1,3}(\.\d{1,3}){3}$"#, options: .regularExpression) != nil
    }

    static func smbPath(_ p: String) -> String {
        p.split(separator: "/").joined(separator: "\\")
    }

    static func isUnreachable(_ e: Error) -> Bool {
        e is NWError || e is POSIXError || e is ConnectionError || e is Timeout || e is Unreachable
    }

    /// AUTH, NO_SHARE, NOT_FOUND, UNREACHABLE, CANCELLED or ERROR, as on Android.
    static func code(_ e: Error) -> String {
        if e is DownloadCancelled || e is CancellationError { return "CANCELLED" }
        if let r = e as? ErrorResponse {
            switch r.header.status {
            case 0xC000006D, 0xC0000022, 0xC000006A, 0xC0000072, 0xC000015B, 0xC0000071, 0xC000006E, 0xC0000064:
                return "AUTH"
            case 0xC00000CC: return "NO_SHARE"
            case 0xC0000034, 0xC000003A: return "NOT_FOUND"
            default: return "ERROR"
            }
        }
        return isUnreachable(e) ? "UNREACHABLE" : "ERROR"
    }

    static func message(_ e: Error) -> String {
        (e as? LocalizedError)?.errorDescription ?? String(describing: e)
    }
}

// MARK: - clients

struct Creds {
    let username: String
    let password: String
    let domain: String
}

/// One signed-in SMBClient. SMBClient isn't safe to use from two tasks at
/// once, so every operation on it runs in turn, and each share keeps one
/// TreeAccessor so that a folder listing doesn't reconnect the share.
actor ClientBox {
    let client: SMBClient
    private var tail: Task<Void, Never>?
    private var trees: [String: TreeAccessor] = [:]

    init(client: SMBClient) { self.client = client }

    func run<T>(_ op: @escaping (SMBClient) async throws -> T) async throws -> T {
        let previous = tail
        let client = self.client
        let task = Task { () async throws -> T in
            _ = await previous?.value
            return try await op(client)
        }
        tail = Task { _ = try? await task.value }
        return try await task.value
    }

    func tree(_ share: String, _ client: SMBClient) -> TreeAccessor {
        if let t = trees[share] { return t }
        let t = client.treeAccessor(share: share)
        trees[share] = t
        return t
    }
}

actor ClientPool {
    private var boxes: [String: ClientBox] = [:]

    func client(_ address: String, _ c: Creds?) async throws -> ClientBox {
        let key = address + "|" + (c.map { "\($0.domain)\\\($0.username)\\\($0.password.hashValue)" } ?? "")
        if let b = boxes[key] { return b }
        let client = SMBClient(host: address)
        client.onDisconnected = { [weak self] _ in
            Task { await self?.forget(key) }
        }
        let user = (c?.username.isEmpty ?? true) ? "guest" : c!.username
        let pass = (c?.username.isEmpty ?? true) ? "" : c!.password
        let domain = (c?.domain.isEmpty ?? true) ? nil : c!.domain
        _ = try await withTimeout(15) {
            try await client.login(username: user, password: pass, domain: domain)
        }
        let box = ClientBox(client: client)
        boxes[key] = box
        return box
    }

    private func forget(_ key: String) { boxes.removeValue(forKey: key) }

    func drop(_ address: String) {
        for k in boxes.keys where k.hasPrefix(address + "|") { boxes.removeValue(forKey: k) }
    }

    func dropAll() { boxes.removeAll() }
}

/// Fail with Timeout when `op` takes longer than `seconds`: an address that
/// nobody answers on can otherwise keep a TCP connect waiting for a minute.
func withTimeout<T>(_ seconds: Double, _ op: @escaping () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await op() }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            throw PolarisSmbPlugin.Timeout()
        }
        defer { group.cancelAll() }
        return try await group.next()!
    }
}

actor DownloadFlags {
    private var cancelled: [String: Bool] = [:]
    func start(_ id: String) { cancelled[id] = false }
    func cancel(_ id: String) { if cancelled[id] != nil { cancelled[id] = true } }
    func isCancelled(_ id: String) -> Bool { cancelled[id] ?? false }
    func finish(_ id: String) { cancelled.removeValue(forKey: id) }
}

// MARK: - Keychain

enum Keychain {
    private static let service = "dev.danwbr.polaris.smb"

    private static func account(_ server: String) -> String {
        server.trimmingCharacters(in: .whitespaces).lowercased()
    }

    static func save(server: String, _ c: Creds) throws {
        let blob = try JSONSerialization.data(withJSONObject: ["u": c.username, "p": c.password, "d": c.domain])
        forget(server: server)
        let add: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account(server),
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecValueData as String: blob,
        ]
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "The Keychain refused to save the password (\(status))."])
        }
    }

    static func load(server: String) -> Creds? {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account(server),
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data,
              let o = try? JSONSerialization.jsonObject(with: data) as? [String: String] else { return nil }
        return Creds(username: o["u"] ?? "", password: o["p"] ?? "", domain: o["d"] ?? "")
    }

    static func forget(server: String) {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account(server),
        ]
        SecItemDelete(q as CFDictionary)
    }
}
