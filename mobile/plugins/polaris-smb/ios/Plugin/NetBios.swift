import Foundation
import Darwin

/// NetBIOS node status (RFC 1002), the "what is your name?" question, sent to
/// every address of the Wi-Fi subnet. Windows, Samba and most NAS boxes answer
/// it without any setup.
///
/// The Android plugin (NetBios.java) also has the broadcast "who has this
/// name?" query. iOS only lets an app send broadcast UDP with the multicast
/// entitlement, which Apple grants on request, so on iOS a name is found by
/// sweeping the subnet with unicast questions instead, and unicast only needs
/// the local network permission the app already asks for.
enum NetBios {
    private static let port: UInt16 = 137

    /// The Wi-Fi (or wired) IPv4 address of this device, as four bytes.
    static func localIPv4() -> [UInt8]? {
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return nil }
        defer { freeifaddrs(ifaddr) }
        var fallback: [UInt8]?
        for p in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let ifa = p.pointee
            guard let sa = ifa.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET) else { continue }
            let name = String(cString: ifa.ifa_name)
            let addr = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr.s_addr }
            let bytes = withUnsafeBytes(of: addr) { Array($0) }   // network order in memory
            if bytes[0] == 127 { continue }
            if name == "en0" { return bytes }
            if name.hasPrefix("en") && fallback == nil { fallback = bytes }
        }
        return fallback
    }

    /// Ask every address of this device's /24 for its name: [address: name].
    static func sweep(timeout: TimeInterval) -> [(address: String, name: String)] {
        guard let me = localIPv4() else { return [] }
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { return [] }
        defer { close(fd) }
        var tv = timeval(tv_sec: 0, tv_usec: 150_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        let id = UInt16.random(in: 1...0xFFFE)
        let q = buildNodeStatusRequest(id: id)
        for h in 1...254 where UInt8(h) != me[3] {
            send(fd, q, to: [me[0], me[1], me[2], UInt8(h)])
        }
        var found: [(String, String)] = []
        var seen = Set<String>()
        let end = Date().addingTimeInterval(timeout)
        while Date() < end {
            guard let got = receive(fd) else { continue }
            let reply = got.0, from = got.1
            guard isAnswer(reply, to: id), let name = parseNodeStatusResponse(reply), !seen.contains(from) else { continue }
            seen.insert(from)
            found.append((from, name))
        }
        return found.map { (address: $0.0, name: $0.1) }
    }

    /// The address of the machine called `name` on this subnet, or nil.
    static func find(name: String, timeout: TimeInterval) -> String? {
        let want = name.uppercased()
        return sweep(timeout: timeout).first { $0.name.uppercased() == want }?.address
    }

    /// The name of the machine at `address`, or nil when it doesn't answer.
    static func nameOf(address: String, timeout: TimeInterval) -> String? {
        let parts = address.split(separator: ".").compactMap { UInt8($0) }
        guard parts.count == 4 else { return nil }
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        var tv = timeval(tv_sec: 0, tv_usec: 150_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        let id = UInt16.random(in: 1...0xFFFE)
        send(fd, buildNodeStatusRequest(id: id), to: parts)
        let end = Date().addingTimeInterval(timeout)
        while Date() < end {
            if let got = receive(fd), isAnswer(got.0, to: id) { return parseNodeStatusResponse(got.0) }
        }
        return nil
    }

    // MARK: - sockets

    private static func send(_ fd: Int32, _ packet: [UInt8], to ip: [UInt8]) {
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr.s_addr = ip.withUnsafeBytes { $0.load(as: UInt32.self) }
        _ = packet.withUnsafeBytes { p in
            withUnsafePointer(to: &addr) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, p.baseAddress, p.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    private static func receive(_ fd: Int32) -> ([UInt8], String)? {
        var buf = [UInt8](repeating: 0, count: 2048)
        var from = sockaddr_in()
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        let n = withUnsafeMutablePointer(to: &from) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) }
        }
        guard n > 0 else { return nil }
        let b = withUnsafeBytes(of: from.sin_addr.s_addr) { Array($0) }
        return (Array(buf[0..<n]), "\(b[0]).\(b[1]).\(b[2]).\(b[3])")
    }

    // MARK: - packets (same layout as NetBiosNames.cs)

    private static func isAnswer(_ r: [UInt8], to id: UInt16) -> Bool {
        r.count >= 12 && (UInt16(r[0]) << 8 | UInt16(r[1])) == id && (r[2] & 0x80) != 0
    }

    static func buildNodeStatusRequest(id: UInt16) -> [UInt8] {
        var raw = [UInt8](repeating: 0, count: 16)
        raw[0] = UInt8(ascii: "*")
        var name: [UInt8] = [32]
        for b in raw {
            name.append(UInt8(ascii: "A") + (b >> 4))
            name.append(UInt8(ascii: "A") + (b & 0x0F))
        }
        name.append(0)
        var p = [UInt8](repeating: 0, count: 12)
        p[0] = UInt8(id >> 8); p[1] = UInt8(id & 0xFF)
        p[5] = 1
        p += name
        p += [0x00, 0x21, 0x00, 0x01]   // NBSTAT, class IN
        return p
    }

    /// The unique file server name (suffix 0x20) if listed, else the workstation name (0x00).
    static func parseNodeStatusResponse(_ r: [UInt8]) -> String? {
        guard r.count >= 12, (Int(r[6]) << 8 | Int(r[7])) >= 1 else { return nil }
        var i = 12
        while i < r.count {
            let len = Int(r[i])
            if len == 0 { i += 1; break }
            if len & 0xC0 == 0xC0 { i += 2; break }
            i += 1 + len
        }
        i += 2 + 2 + 4
        guard i + 2 <= r.count else { return nil }
        let rdLength = Int(r[i]) << 8 | Int(r[i + 1])
        i += 2
        guard rdLength >= 1, i + rdLength <= r.count else { return nil }
        let count = Int(r[i]); i += 1
        var workstation: String?
        for _ in 0..<count {
            guard i + 18 <= r.count else { break }
            let raw = String(bytes: r[i..<(i + 15)], encoding: .ascii) ?? ""
            let name = raw.trimmingCharacters(in: CharacterSet(charactersIn: " \0"))
            let suffix = r[i + 15]
            let group = (r[i + 16] & 0x80) != 0
            i += 18
            if group || name.isEmpty { continue }
            if suffix == 0x20 { return name }
            if suffix == 0x00 && workstation == nil { workstation = name }
        }
        return workstation
    }
}
