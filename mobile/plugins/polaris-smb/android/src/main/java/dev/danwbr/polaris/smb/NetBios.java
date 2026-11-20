package dev.danwbr.polaris.smb;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.Random;

/**
 * The NetBIOS name service questions (RFC 1002) that find a file server on
 * the local network: "what is your name?" asked of an address (node status)
 * and "who has this name?" asked of the subnet (broadcast name query).
 * Windows, Samba and most NAS boxes answer both without any setup.
 *
 * A port of src/NINA.Polaris/Services/Storage/NetBiosNames.cs, so that the app
 * finds a server the same way the Polaris host's SMB push does. The app also
 * needs it because Android does not resolve a bare NetBIOS name such as
 * "JUPITER" by itself.
 */
final class NetBios {
    private static final int PORT = 137;
    private static final Random RNG = new Random();

    private NetBios() { }

    /** The IPv4 address answering for {@code name}, or null when nobody does. */
    static InetAddress resolve(String name, InetAddress broadcast, int timeoutMs) {
        int id = 1 + RNG.nextInt(0xFFFE);
        try (DatagramSocket s = new DatagramSocket()) {
            s.setBroadcast(true);
            s.setSoTimeout(timeoutMs);
            byte[] q = buildNameQuery(id, name);
            s.send(new DatagramPacket(q, q.length, broadcast, PORT));
            long end = System.currentTimeMillis() + timeoutMs;
            byte[] buf = new byte[1024];
            while (System.currentTimeMillis() < end) {
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                try { s.receive(p); } catch (SocketTimeoutException e) { return null; }
                byte[] r = slice(p);
                if (isAnswerTo(r, id)) {
                    InetAddress a = parseNameQueryResponse(r);
                    if (a != null) return a;
                }
            }
        } catch (Exception ignored) {
            // A closed or unreachable network is the same as "nobody answered".
        }
        return null;
    }

    /** The NetBIOS name of the machine at {@code address}, or null when it doesn't answer. */
    static String nameOf(InetAddress address, int timeoutMs) {
        int id = 1 + RNG.nextInt(0xFFFE);
        try (DatagramSocket s = new DatagramSocket()) {
            s.setSoTimeout(timeoutMs);
            byte[] q = buildNodeStatusRequest(id);
            s.send(new DatagramPacket(q, q.length, address, PORT));
            byte[] buf = new byte[2048];
            long end = System.currentTimeMillis() + timeoutMs;
            while (System.currentTimeMillis() < end) {
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                try { s.receive(p); } catch (SocketTimeoutException e) { return null; }
                byte[] r = slice(p);
                if (isAnswerTo(r, id)) return parseNodeStatusResponse(r);
            }
        } catch (Exception ignored) {
            // Unreachable is the same as "no answer".
        }
        return null;
    }

    /**
     * Ask every address of {@code subnet} (a /24 given by its first three
     * octets) for its name and collect the answers for {@code timeoutMs}.
     * Returns address to name, in answer order.
     */
    static Map<String, String> sweep(byte[] subnet, byte self, int timeoutMs) {
        Map<String, String> found = new LinkedHashMap<>();
        try (DatagramSocket s = new DatagramSocket()) {
            s.setSoTimeout(150);
            int id = 1 + RNG.nextInt(0xFFFE);
            byte[] q = buildNodeStatusRequest(id);
            for (int h = 1; h < 255; h++) {
                if ((byte) h == self) continue;
                InetAddress to = InetAddress.getByAddress(new byte[] { subnet[0], subnet[1], subnet[2], (byte) h });
                s.send(new DatagramPacket(q, q.length, to, PORT));
            }
            long end = System.currentTimeMillis() + timeoutMs;
            byte[] buf = new byte[2048];
            while (System.currentTimeMillis() < end) {
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                try { s.receive(p); } catch (SocketTimeoutException e) { continue; }
                byte[] r = slice(p);
                if (!isAnswerTo(r, id)) continue;
                String name = parseNodeStatusResponse(r);
                if (name != null) found.put(p.getAddress().getHostAddress(), name);
            }
        } catch (Exception ignored) {
            // Return what answered before the network went away.
        }
        return found;
    }

    // ---- packets ----

    private static byte[] slice(DatagramPacket p) {
        byte[] r = new byte[p.getLength()];
        System.arraycopy(p.getData(), p.getOffset(), r, 0, r.length);
        return r;
    }

    private static boolean isAnswerTo(byte[] r, int id) {
        return r.length >= 12 && (((r[0] & 0xFF) << 8) | (r[1] & 0xFF)) == id && (r[2] & 0x80) != 0;
    }

    /** RFC 1002 first-level encoding: each of the 16 bytes becomes two letters 'A'..'P'. */
    static byte[] encodeName(byte[] raw16) {
        byte[] o = new byte[34];
        o[0] = 32;
        for (int i = 0; i < 16; i++) {
            o[1 + 2 * i] = (byte) ('A' + ((raw16[i] & 0xFF) >> 4));
            o[2 + 2 * i] = (byte) ('A' + (raw16[i] & 0x0F));
        }
        return o;
    }

    /** Upper case, padded with spaces to fifteen characters, then the suffix byte. */
    static byte[] rawName(String name, int suffix) {
        byte[] raw = new byte[16];
        byte[] up = name.trim().toUpperCase(Locale.ROOT).getBytes(StandardCharsets.US_ASCII);
        for (int i = 0; i < 15; i++) raw[i] = i < up.length ? up[i] : (byte) ' ';
        raw[15] = (byte) suffix;
        return raw;
    }

    static byte[] buildNodeStatusRequest(int id) {
        byte[] raw = new byte[16];
        raw[0] = (byte) '*';
        return packet(id, 0x0000, encodeName(raw), 0x0021);
    }

    /** Recursion desired + broadcast; suffix 0x20 is the file server service. */
    static byte[] buildNameQuery(int id, String name) {
        return packet(id, 0x0110, encodeName(rawName(name, 0x20)), 0x0020);
    }

    private static byte[] packet(int id, int flags, byte[] encodedName, int qtype) {
        byte[] p = new byte[12 + encodedName.length + 4];
        p[0] = (byte) (id >> 8); p[1] = (byte) id;
        p[2] = (byte) (flags >> 8); p[3] = (byte) flags;
        p[5] = 1;
        System.arraycopy(encodedName, 0, p, 12, encodedName.length);
        int q = 12 + encodedName.length;
        p[q] = (byte) (qtype >> 8); p[q + 1] = (byte) qtype;
        p[q + 3] = 1;
        return p;
    }

    /** The unique file server name (suffix 0x20) if listed, else the workstation name (0x00). */
    static String parseNodeStatusResponse(byte[] r) {
        try {
            if (answerCount(r) < 1) return null;
            int i = skipName(r, 12);
            i += 2 + 2 + 4;
            int rdLength = ((r[i] & 0xFF) << 8) | (r[i + 1] & 0xFF);
            i += 2;
            if (rdLength < 1 || i + rdLength > r.length) return null;
            int count = r[i++] & 0xFF;
            String workstation = null;
            for (int n = 0; n < count && i + 18 <= r.length; n++, i += 18) {
                String name = new String(r, i, 15, StandardCharsets.US_ASCII).replaceAll("[ \\x00]+$", "");
                int suffix = r[i + 15] & 0xFF;
                boolean group = (r[i + 16] & 0x80) != 0;
                if (group || name.isEmpty()) continue;
                if (suffix == 0x20) return name;
                if (suffix == 0x00 && workstation == null) workstation = name;
            }
            return workstation;
        } catch (ArrayIndexOutOfBoundsException e) {
            return null;
        }
    }

    static InetAddress parseNameQueryResponse(byte[] r) {
        try {
            if ((r[3] & 0x0F) != 0 || answerCount(r) < 1) return null;
            int i = skipName(r, 12);
            i += 2 + 2 + 4;
            int rdLength = ((r[i] & 0xFF) << 8) | (r[i + 1] & 0xFF);
            i += 2;
            if (rdLength < 6 || i + 6 > r.length) return null;
            return InetAddress.getByAddress(new byte[] { r[i + 2], r[i + 3], r[i + 4], r[i + 5] });
        } catch (Exception e) {
            return null;
        }
    }

    private static int answerCount(byte[] r) {
        return r.length < 12 ? 0 : ((r[6] & 0xFF) << 8) | (r[7] & 0xFF);
    }

    private static int skipName(byte[] r, int i) {
        while (true) {
            int len = r[i] & 0xFF;
            if (len == 0) return i + 1;
            if ((len & 0xC0) == 0xC0) return i + 2;
            i += 1 + len;
        }
    }
}
