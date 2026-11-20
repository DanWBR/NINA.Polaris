package dev.danwbr.polaris.smb;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * SMB user names and passwords, one per server. Each entry is encrypted with
 * an AES key that lives in the Android Keystore, so the password never sits
 * in plain text in the app's storage, and the page's JavaScript never gets it
 * back: it can only ask whether a server has saved credentials.
 */
final class CredentialStore {
    static final class Creds {
        final String username, password, domain;
        Creds(String username, String password, String domain) {
            this.username = username == null ? "" : username;
            this.password = password == null ? "" : password;
            this.domain = domain == null ? "" : domain;
        }
    }

    private static final String ALIAS = "polaris-smb-credentials";
    private static final String PREFS = "polaris_smb_credentials";
    private final SharedPreferences prefs;

    CredentialStore(Context ctx) {
        prefs = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    synchronized void save(String server, Creds c) throws Exception {
        JSONObject o = new JSONObject();
        o.put("u", c.username);
        o.put("p", c.password);
        o.put("d", c.domain);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] ct = cipher.doFinal(o.toString().getBytes(StandardCharsets.UTF_8));
        String blob = Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":"
            + Base64.encodeToString(ct, Base64.NO_WRAP);
        prefs.edit().putString(keyFor(server), blob).apply();
    }

    synchronized Creds load(String server) {
        String blob = prefs.getString(keyFor(server), null);
        if (blob == null) return null;
        try {
            String[] parts = blob.split(":", 2);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
            JSONObject o = new JSONObject(new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), StandardCharsets.UTF_8));
            return new Creds(o.optString("u"), o.optString("p"), o.optString("d"));
        } catch (Exception e) {
            // The key was lost (the user cleared credentials, or a restore moved
            // the data to another phone): the entry can't be read, so drop it.
            forget(server);
            return null;
        }
    }

    synchronized void forget(String server) {
        prefs.edit().remove(keyFor(server)).apply();
    }

    private static String keyFor(String server) {
        return "srv:" + server.trim().toLowerCase(java.util.Locale.ROOT);
    }

    private static SecretKey key() throws Exception {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if (ks.containsAlias(ALIAS)) return ((KeyStore.SecretKeyEntry) ks.getEntry(ALIAS, null)).getSecretKey();
        KeyGenerator kg = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        kg.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .build());
        return kg.generateKey();
    }
}
