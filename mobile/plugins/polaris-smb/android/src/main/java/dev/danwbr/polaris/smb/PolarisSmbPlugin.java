package dev.danwbr.polaris.smb;

import android.content.Context;
import android.database.Cursor;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.Uri;
import android.provider.OpenableColumns;

import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import com.hierynomus.msdtyp.AccessMask;
import com.hierynomus.msfscc.FileAttributes;
import com.hierynomus.msfscc.fileinformation.FileIdBothDirectoryInformation;
import com.hierynomus.mssmb2.SMB2CreateDisposition;
import com.hierynomus.mssmb2.SMB2ShareAccess;
import com.hierynomus.mssmb2.SMBApiException;
import com.hierynomus.security.bc.BCSecurityProvider;
import com.hierynomus.smbj.SMBClient;
import com.hierynomus.smbj.SmbConfig;
import com.hierynomus.smbj.auth.AuthenticationContext;
import com.hierynomus.smbj.connection.Connection;
import com.hierynomus.smbj.session.Session;
import com.hierynomus.smbj.share.DiskShare;
import com.hierynomus.smbj.share.Share;

import com.rapid7.client.dcerpc.mssrvs.ServerService;
import com.rapid7.client.dcerpc.mssrvs.dto.NetShareInfo1;
import com.rapid7.client.dcerpc.transport.RPCTransport;
import com.rapid7.client.dcerpc.transport.SMBTransportFactories;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ConnectException;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NoRouteToHostException;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.EnumSet;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Read-only SMB access for the app's Files page, so that FITS files on a
 * network share can be looked at with no Polaris host running.
 *
 * Every method takes the server as { host, name? }: host is what the user
 * typed or what discovery found (an address or a name), and name is the
 * server's NetBIOS name when it is known. When the host doesn't answer and a
 * name is known, the plugin looks the name up again, because a DHCP lease
 * moves a file server to another address. Results carry the address that
 * worked, so that the page can remember it.
 *
 * Credentials are passed once with remember: true and then kept by
 * CredentialStore under the server's id; the page never reads them back.
 * Without credentials the plugin signs in as guest.
 *
 * Downloads go to the cache directory and the page reads them through
 * Capacitor.convertFileSrc(path), so a 50 MB frame never crosses the bridge as
 * base64. The page calls releaseFile once it has the bytes.
 */
@CapacitorPlugin(name = "PolarisSmb")
public class PolarisSmbPlugin extends Plugin {

    // Capacitor runs every plugin call of every plugin on one handler thread,
    // so network work happens here instead.
    private final ExecutorService io = Executors.newFixedThreadPool(3);
    private final Map<String, Session> sessions = new HashMap<>();
    private final Map<String, AtomicBoolean> downloads = new ConcurrentHashMap<>();
    private SMBClient client;
    private CredentialStore creds;
    private File cacheDir;

    private interface Task { void run() throws Exception; }

    @Override
    public void load() {
        client = new SMBClient(SmbConfig.builder()
            // Android's JCE has no MD4, which NTLM needs.
            .withSecurityProvider(new BCSecurityProvider())
            .withMultiProtocolNegotiate(true)
            .withTimeout(20, TimeUnit.SECONDS)
            .withSoTimeout(30, TimeUnit.SECONDS)
            .build());
        creds = new CredentialStore(getContext());
        cacheDir = new File(getContext().getCacheDir(), "smb");
        // Files from an earlier run that the page never released.
        File[] old = cacheDir.listFiles();
        if (old != null) for (File f : old) f.delete();
        cacheDir.mkdirs();
    }

    @Override
    protected void handleOnDestroy() {
        io.shutdownNow();
        try { client.close(); } catch (Exception ignored) { }
    }

    private void run(PluginCall call, Task t) {
        io.execute(() -> {
            try {
                t.run();
            } catch (Throwable e) {
                call.reject(message(e), code(e));
            }
        });
    }

    // ---------------------------------------------------------------- discovery

    /** NetBIOS sweep of the Wi-Fi subnet: [{ name, address }]. */
    @PluginMethod
    public void discover(PluginCall call) {
        int timeout = call.getInt("timeoutMs", 1500);
        run(call, () -> {
            Inet4Address self = localIpv4();
            JSArray out = new JSArray();
            if (self != null) {
                byte[] a = self.getAddress();
                Map<String, String> found = NetBios.sweep(a, a[3], timeout);
                List<String> addrs = new ArrayList<>(found.keySet());
                Collections.sort(addrs, (x, y) -> found.get(x).compareToIgnoreCase(found.get(y)));
                for (String addr : addrs) {
                    JSObject o = new JSObject();
                    o.put("name", found.get(addr));
                    o.put("address", addr);
                    out.put(o);
                }
            }
            JSObject r = new JSObject();
            r.put("servers", out);
            r.put("onNetwork", self != null);
            call.resolve(r);
        });
    }

    // ------------------------------------------------------------- credentials

    @PluginMethod
    public void hasCredentials(PluginCall call) {
        String id = call.getString("server", "");
        run(call, () -> {
            CredentialStore.Creds c = creds.load(id);
            JSObject r = new JSObject();
            r.put("saved", c != null);
            r.put("username", c == null ? "" : c.username);
            call.resolve(r);
        });
    }

    @PluginMethod
    public void forgetCredentials(PluginCall call) {
        String id = call.getString("server", "");
        run(call, () -> {
            creds.forget(id);
            dropSessions(null);
            call.resolve();
        });
    }

    // ------------------------------------------------------------------- browse

    /** The disk shares a server offers: { address, name, shares: [{ name, comment }] }. */
    @PluginMethod
    public void listShares(PluginCall call) {
        run(call, () -> withSession(call, (session, address) -> {
            RPCTransport transport = SMBTransportFactories.SRVSVC.getTransport(session);
            List<NetShareInfo1> list = new ServerService(transport).getShares1();
            JSArray shares = new JSArray();
            for (NetShareInfo1 s : list) {
                String n = s.getNetName();
                // Type 0 is a disk share; a trailing $ marks an admin or hidden one.
                if (n == null || n.endsWith("$") || (s.getType() & 0x0FFFFFFF) != 0) continue;
                JSObject o = new JSObject();
                o.put("name", n);
                o.put("comment", s.getRemark() == null ? "" : s.getRemark());
                shares.put(o);
            }
            JSObject r = new JSObject();
            r.put("address", address);
            r.put("name", serverName(call, address));
            r.put("shares", shares);
            call.resolve(r);
        }));
    }

    /** A folder's entries: [{ name, dir, size, modified }], folders first. */
    @PluginMethod
    public void list(PluginCall call) {
        String shareName = call.getString("share", "");
        String path = smbPath(call.getString("path", ""));
        run(call, () -> withSession(call, (session, address) -> {
            try (DiskShare share = (DiskShare) session.connectShare(shareName)) {
                List<JSObject> dirs = new ArrayList<>(), files = new ArrayList<>();
                for (FileIdBothDirectoryInformation f : share.list(path)) {
                    String n = f.getFileName();
                    if (n.equals(".") || n.equals("..")) continue;
                    long attrs = f.getFileAttributes();
                    if ((attrs & FileAttributes.FILE_ATTRIBUTE_HIDDEN.getValue()) != 0) continue;
                    boolean dir = (attrs & FileAttributes.FILE_ATTRIBUTE_DIRECTORY.getValue()) != 0;
                    JSObject o = new JSObject();
                    o.put("name", n);
                    o.put("dir", dir);
                    o.put("size", dir ? 0 : f.getEndOfFile());
                    o.put("modified", f.getLastWriteTime().toEpochMillis());
                    (dir ? dirs : files).add(o);
                }
                JSArray entries = new JSArray();
                for (JSObject o : dirs) entries.put(o);
                for (JSObject o : files) entries.put(o);
                JSObject r = new JSObject();
                r.put("address", address);
                r.put("entries", entries);
                call.resolve(r);
            }
        }));
    }

    /**
     * Copy one file to the cache: { path, size }. Sends "downloadProgress"
     * { id, received, total } while it runs; cancelDownload({ id }) stops it.
     */
    @PluginMethod
    public void download(PluginCall call) {
        String id = call.getString("id", "");
        String shareName = call.getString("share", "");
        String path = smbPath(call.getString("path", ""));
        AtomicBoolean cancelled = new AtomicBoolean(false);
        downloads.put(id, cancelled);
        run(call, () -> {
            try {
                withSession(call, (session, address) -> {
                    String leaf = path.substring(path.lastIndexOf('\\') + 1).replaceAll("[^A-Za-z0-9._-]", "_");
                    File out = File.createTempFile("dl", "-" + leaf, cacheDir);
                    boolean ok = false;
                    try (DiskShare share = (DiskShare) session.connectShare(shareName);
                         com.hierynomus.smbj.share.File f = share.openFile(path,
                             EnumSet.of(AccessMask.GENERIC_READ), null, SMB2ShareAccess.ALL,
                             SMB2CreateDisposition.FILE_OPEN, null);
                         InputStream in = f.getInputStream();
                         OutputStream os = new FileOutputStream(out)) {
                        long total = f.getFileInformation().getStandardInformation().getEndOfFile();
                        byte[] buf = new byte[1 << 20];
                        long received = 0, lastNote = 0;
                        int n;
                        while ((n = in.read(buf)) > 0) {
                            if (cancelled.get()) throw new InterruptedException("cancelled");
                            os.write(buf, 0, n);
                            received += n;
                            long now = System.currentTimeMillis();
                            if (now - lastNote > 200) {
                                lastNote = now;
                                JSObject p = new JSObject();
                                p.put("id", id);
                                p.put("received", received);
                                p.put("total", total);
                                notifyListeners("downloadProgress", p);
                            }
                        }
                        ok = true;
                        JSObject r = new JSObject();
                        r.put("address", address);
                        r.put("path", out.getAbsolutePath());
                        r.put("size", received);
                        call.resolve(r);
                    } finally {
                        if (!ok) out.delete();
                    }
                });
            } finally {
                downloads.remove(id);
            }
        });
    }

    @PluginMethod
    public void cancelDownload(PluginCall call) {
        AtomicBoolean c = downloads.get(call.getString("id", ""));
        if (c != null) c.set(true);
        call.resolve();
    }

    /** Delete a file download() wrote. Only files in the plugin's cache folder. */
    @PluginMethod
    public void releaseFile(PluginCall call) {
        String p = call.getString("path", "");
        File f = new File(p);
        try {
            if (f.getCanonicalFile().getParentFile().equals(cacheDir.getCanonicalFile())) f.delete();
        } catch (IOException ignored) { }
        call.resolve();
    }

    // -------------------------------------------------------------- open with

    /** Name and size of a content:// or file:// URI that "Open with" handed the app. */
    @PluginMethod
    public void describeUri(PluginCall call) {
        String s = call.getString("uri", "");
        run(call, () -> {
            Uri uri = Uri.parse(s);
            String name = null;
            long size = -1;
            if ("content".equals(uri.getScheme())) {
                try (Cursor c = getContext().getContentResolver().query(uri,
                        new String[] { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE }, null, null, null)) {
                    if (c != null && c.moveToFirst()) {
                        int ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                        int si = c.getColumnIndex(OpenableColumns.SIZE);
                        if (ni >= 0) name = c.getString(ni);
                        if (si >= 0 && !c.isNull(si)) size = c.getLong(si);
                    }
                }
            } else if ("file".equals(uri.getScheme()) && uri.getPath() != null) {
                File f = new File(uri.getPath());
                name = f.getName();
                size = f.length();
            }
            if (name == null) name = uri.getLastPathSegment();
            JSObject r = new JSObject();
            r.put("name", name == null ? "" : name);
            r.put("size", size);
            call.resolve(r);
        });
    }

    // ---------------------------------------------------------------- sessions

    private interface SessionWork { void run(Session session, String address) throws Exception; }

    /**
     * Run work on a signed-in session to the call's server, reusing an open
     * one. When the connection fails, it re-resolves a known NetBIOS name once
     * and tries the new address.
     */
    private void withSession(PluginCall call, SessionWork work) throws Exception {
        String host = call.getString("host", "").trim();
        String name = call.getString("name", "");
        String server = call.getString("server", host);
        CredentialStore.Creds c = credsFor(call, server);

        String address;
        try {
            address = resolveHost(host);
        } catch (UnknownHostException e) {
            InetAddress a = name == null || name.isEmpty() ? null : NetBios.resolve(name, broadcast(), 1500);
            if (a == null) throw e;
            address = a.getHostAddress();
        }
        try {
            work.run(session(address, c), address);
        } catch (Exception e) {
            if (!isUnreachable(e) || name == null || name.isEmpty()) {
                if (!(e instanceof SMBApiException)) dropSessions(address);
                throw e;
            }
            dropSessions(address);
            InetAddress a = NetBios.resolve(name, broadcast(), 1500);
            if (a == null || a.getHostAddress().equals(address)) throw e;
            address = a.getHostAddress();
            work.run(session(address, c), address);
        }
    }

    private CredentialStore.Creds credsFor(PluginCall call, String server) throws Exception {
        String user = call.getString("username");
        if (user != null) {
            CredentialStore.Creds c = new CredentialStore.Creds(user, call.getString("password", ""), call.getString("domain", ""));
            if (Boolean.TRUE.equals(call.getBoolean("remember", false))) creds.save(server, c);
            return c;
        }
        return creds.load(server);
    }

    private synchronized Session session(String address, CredentialStore.Creds c) throws IOException {
        String key = address + "|" + (c == null ? "" : c.domain + "\\" + c.username + "\\" + c.password.hashCode());
        Session s = sessions.get(key);
        if (s != null && s.getConnection().isConnected()) return s;
        sessions.remove(key);
        Connection conn = client.connect(address);
        AuthenticationContext ac = c == null || c.username.isEmpty()
            ? AuthenticationContext.guest()
            : new AuthenticationContext(c.username, c.password.toCharArray(), c.domain);
        s = conn.authenticate(ac);
        sessions.put(key, s);
        return s;
    }

    private synchronized void dropSessions(String address) {
        sessions.entrySet().removeIf(e -> {
            if (address != null && !e.getKey().startsWith(address + "|")) return false;
            try { e.getValue().close(); } catch (Exception ignored) { }
            return true;
        });
    }

    private String resolveHost(String host) throws UnknownHostException {
        if (host.matches("\\d{1,3}(\\.\\d{1,3}){3}")) return host;
        if (!host.contains(".")) {
            // A bare name such as JUPITER: Android's resolver doesn't do NetBIOS.
            InetAddress a = NetBios.resolve(host, broadcast(), 1500);
            if (a != null) return a.getHostAddress();
        }
        return InetAddress.getByName(host).getHostAddress();
    }

    private String serverName(PluginCall call, String address) {
        String n = call.getString("name", "");
        if (n != null && !n.isEmpty()) return n;
        String host = call.getString("host", "");
        if (!host.matches("\\d{1,3}(\\.\\d{1,3}){3}") && !host.contains(".")) return host.toUpperCase(Locale.ROOT);
        try {
            String found = NetBios.nameOf(InetAddress.getByName(address), 800);
            return found == null ? "" : found;
        } catch (Exception e) {
            return "";
        }
    }

    // ----------------------------------------------------------------- network

    private Inet4Address localIpv4() {
        ConnectivityManager cm = (ConnectivityManager) getContext().getSystemService(Context.CONNECTIVITY_SERVICE);
        Network net = cm == null ? null : cm.getActiveNetwork();
        LinkProperties lp = net == null ? null : cm.getLinkProperties(net);
        if (lp == null) return null;
        for (LinkAddress la : lp.getLinkAddresses()) {
            if (la.getAddress() instanceof Inet4Address && !la.getAddress().isLoopbackAddress())
                return (Inet4Address) la.getAddress();
        }
        return null;
    }

    /** The /24 broadcast of the Wi-Fi network, or the limited broadcast without one. */
    private InetAddress broadcast() {
        try {
            Inet4Address self = localIpv4();
            if (self == null) return InetAddress.getByName("255.255.255.255");
            byte[] a = self.getAddress();
            return InetAddress.getByAddress(new byte[] { a[0], a[1], a[2], (byte) 255 });
        } catch (UnknownHostException e) {
            return null;
        }
    }

    // ------------------------------------------------------------------ errors

    private static String smbPath(String p) {
        String s = p == null ? "" : p.replace('/', '\\');
        while (s.startsWith("\\")) s = s.substring(1);
        while (s.endsWith("\\")) s = s.substring(0, s.length() - 1);
        return s;
    }

    private static boolean isUnreachable(Throwable e) {
        for (Throwable t = e; t != null; t = t.getCause()) {
            if (t instanceof ConnectException || t instanceof NoRouteToHostException
                || t instanceof SocketTimeoutException || t instanceof UnknownHostException) return true;
        }
        return false;
    }

    /** AUTH, NO_SHARE, NOT_FOUND, UNREACHABLE, CANCELLED or ERROR, for the page to act on. */
    private static String code(Throwable e) {
        for (Throwable t = e; t != null; t = t.getCause()) {
            if (t instanceof InterruptedException) return "CANCELLED";
            if (t instanceof SMBApiException) {
                String s = ((SMBApiException) t).getStatus().name();
                if (s.contains("LOGON_FAILURE") || s.contains("ACCESS_DENIED") || s.contains("WRONG_PASSWORD")
                    || s.contains("ACCOUNT") || s.contains("PASSWORD") || s.contains("LOGON_TYPE")) return "AUTH";
                if (s.contains("BAD_NETWORK_NAME")) return "NO_SHARE";
                if (s.contains("NOT_FOUND")) return "NOT_FOUND";
            }
        }
        return isUnreachable(e) ? "UNREACHABLE" : "ERROR";
    }

    private static String message(Throwable e) {
        String m = e.getMessage();
        for (Throwable t = e.getCause(); (m == null || m.isEmpty()) && t != null; t = t.getCause()) m = t.getMessage();
        return m == null || m.isEmpty() ? e.getClass().getSimpleName() : m;
    }
}
