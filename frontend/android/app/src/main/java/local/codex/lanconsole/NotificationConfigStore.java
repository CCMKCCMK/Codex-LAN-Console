package local.codex.lanconsole;

import android.content.Context;
import android.content.SharedPreferences;
import android.net.Uri;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.io.IOException;
import java.security.GeneralSecurityException;
import java.security.KeyStore;
import java.util.ArrayDeque;
import java.util.HashSet;
import java.util.Set;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Private, encrypted configuration shared by the activity and notification service. */
final class NotificationConfigStore {
    private static final String PREFS = "codex_notification_monitor";
    private static final String KEY_ALIAS = "codex_lan_monitor_credentials_v1";
    private static final String KEY_CREDENTIALS = "credentials";
    private static final String KEY_ENABLED = "enabled";
    private static final String KEY_CURSOR = "cursor";
    private static final String KEY_SEEN_IDS = "seen_ids";
    private static final String KEY_PERMISSION_REQUESTED = "permission_requested";
    private static final String KEY_LAST_ERROR = "last_error";
    private static final String KEY_GENERATION = "generation";
    private static final String KEY_WIDGET_QUOTA = "widget_quota";
    private static final String KEY_WIDGET_QUOTA_SAVED_AT = "widget_quota_saved_at";
    private static final String KEY_WIDGET_QUOTA_ERROR = "widget_quota_error";
    private static final int MAX_SEEN_IDS = 256;

    private NotificationConfigStore() {}

    static synchronized boolean configure(Context context, String server, String token) {
        String normalizedServer = normalizeServer(server);
        if (normalizedServer == null || token == null || !token.matches("(?i)[0-9a-f]{64}")) {
            return false;
        }
        Credentials previous = credentials(context);
        boolean changed = previous == null
                || !previous.server.equals(normalizedServer)
                || !previous.token.equals(token);
        try {
            JSONObject clear = new JSONObject()
                    .put("server", normalizedServer)
                    .put("token", token);
            SharedPreferences.Editor editor = prefs(context).edit()
                    .putString(KEY_CREDENTIALS, encrypt(clear.toString()));
            if (changed) {
                editor
                        .remove(KEY_CURSOR)
                        .remove(KEY_SEEN_IDS)
                        .remove(KEY_LAST_ERROR)
                        .remove(KEY_WIDGET_QUOTA)
                        .remove(KEY_WIDGET_QUOTA_SAVED_AT)
                        .remove(KEY_WIDGET_QUOTA_ERROR)
                        .putLong(KEY_GENERATION, generation(context) + 1L);
            }
            return editor.commit();
        } catch (GeneralSecurityException | JSONException error) {
            return false;
        }
    }

    static synchronized Credentials credentials(Context context) {
        String encoded = prefs(context).getString(KEY_CREDENTIALS, null);
        if (isEmpty(encoded)) {
            return null;
        }
        try {
            JSONObject clear = new JSONObject(decrypt(encoded));
            String server = normalizeServer(clear.optString("server", ""));
            String token = clear.optString("token", "");
            if (server == null || !token.matches("(?i)[0-9a-f]{64}")) {
                throw new GeneralSecurityException("Invalid stored credentials");
            }
            return new Credentials(server, token);
        } catch (GeneralSecurityException | JSONException | IllegalArgumentException error) {
            prefs(context).edit()
                    .remove(KEY_CREDENTIALS)
                    .putBoolean(KEY_ENABLED, false)
                    .remove(KEY_CURSOR)
                    .remove(KEY_SEEN_IDS)
                    .remove(KEY_WIDGET_QUOTA)
                    .remove(KEY_WIDGET_QUOTA_SAVED_AT)
                    .remove(KEY_WIDGET_QUOTA_ERROR)
                    .apply();
            return null;
        }
    }

    static boolean isConfigured(Context context) {
        return credentials(context) != null;
    }

    static boolean credentialsMatch(Context context, String server, String token) {
        Credentials current = credentials(context);
        String normalized = normalizeServer(server);
        return current != null
                && normalized != null
                && current.server.equals(normalized)
                && token != null
                && current.token.equals(token);
    }

    static synchronized long generation(Context context) {
        return prefs(context).getLong(KEY_GENERATION, 0L);
    }

    static boolean isEnabled(Context context) {
        return prefs(context).getBoolean(KEY_ENABLED, false);
    }

    static void setEnabled(Context context, boolean enabled) {
        prefs(context).edit().putBoolean(KEY_ENABLED, enabled).apply();
    }

    static String cursor(Context context) {
        return prefs(context).getString(KEY_CURSOR, "");
    }

    static synchronized boolean setCursor(
            Context context,
            String cursor,
            long expectedGeneration) {
        if (isEmpty(cursor) || generation(context) != expectedGeneration) {
            return false;
        }
        return prefs(context).edit().putString(KEY_CURSOR, cursor).commit();
    }

    static synchronized boolean hasSeenEvent(Context context, String eventId) {
        return !isEmpty(eventId) && readSeenIds(context).contains(eventId);
    }

    /** Records a successfully posted event durably before its feed cursor is advanced. */
    static synchronized boolean rememberEvent(
            Context context,
            String eventId,
            long expectedGeneration) {
        if (isEmpty(eventId) || generation(context) != expectedGeneration) {
            return false;
        }
        ArrayDeque<String> ordered = readSeenIds(context);
        Set<String> membership = new HashSet<>(ordered);
        if (!membership.add(eventId)) {
            return true;
        }
        ordered.addLast(eventId);
        while (ordered.size() > MAX_SEEN_IDS) {
            ordered.removeFirst();
        }
        JSONArray encoded = new JSONArray();
        for (String id : ordered) {
            encoded.put(id);
        }
        return prefs(context).edit()
                .putString(KEY_SEEN_IDS, encoded.toString())
                .commit();
    }

    static synchronized boolean clearSeenEvents(Context context, long expectedGeneration) {
        if (generation(context) != expectedGeneration) {
            return false;
        }
        return prefs(context).edit().remove(KEY_SEEN_IDS).commit();
    }

    static boolean wasPermissionRequested(Context context) {
        return prefs(context).getBoolean(KEY_PERMISSION_REQUESTED, false);
    }

    static void markPermissionRequested(Context context) {
        prefs(context).edit().putBoolean(KEY_PERMISSION_REQUESTED, true).apply();
    }

    static String lastError(Context context) {
        return prefs(context).getString(KEY_LAST_ERROR, "");
    }

    static void setLastError(Context context, String message) {
        SharedPreferences.Editor editor = prefs(context).edit();
        if (isEmpty(message)) {
            editor.remove(KEY_LAST_ERROR);
        } else {
            editor.putString(KEY_LAST_ERROR, message);
        }
        editor.apply();
    }

    static synchronized boolean saveWidgetQuota(Context context, String payload) {
        if (isEmpty(payload)) {
            return false;
        }
        try {
            return prefs(context).edit()
                    .putString(KEY_WIDGET_QUOTA, encrypt(payload))
                    .putLong(KEY_WIDGET_QUOTA_SAVED_AT, System.currentTimeMillis())
                    .remove(KEY_WIDGET_QUOTA_ERROR)
                    .commit();
        } catch (GeneralSecurityException | JSONException error) {
            return false;
        }
    }

    static synchronized String widgetQuota(Context context) {
        String encoded = prefs(context).getString(KEY_WIDGET_QUOTA, null);
        if (isEmpty(encoded)) {
            return "";
        }
        try {
            return decrypt(encoded);
        } catch (GeneralSecurityException | JSONException | IllegalArgumentException error) {
            prefs(context).edit()
                    .remove(KEY_WIDGET_QUOTA)
                    .remove(KEY_WIDGET_QUOTA_SAVED_AT)
                    .apply();
            return "";
        }
    }

    static long widgetQuotaSavedAt(Context context) {
        return prefs(context).getLong(KEY_WIDGET_QUOTA_SAVED_AT, 0L);
    }

    static String widgetQuotaError(Context context) {
        return prefs(context).getString(KEY_WIDGET_QUOTA_ERROR, "");
    }

    static void setWidgetQuotaError(Context context, String message) {
        SharedPreferences.Editor editor = prefs(context).edit();
        if (isEmpty(message)) {
            editor.remove(KEY_WIDGET_QUOTA_ERROR);
        } else {
            editor.putString(KEY_WIDGET_QUOTA_ERROR, message);
        }
        editor.apply();
    }

    private static ArrayDeque<String> readSeenIds(Context context) {
        ArrayDeque<String> result = new ArrayDeque<>();
        try {
            JSONArray encoded = new JSONArray(prefs(context).getString(KEY_SEEN_IDS, "[]"));
            int first = Math.max(0, encoded.length() - MAX_SEEN_IDS);
            for (int index = first; index < encoded.length(); index++) {
                String id = encoded.optString(index, "");
                if (!isEmpty(id)) {
                    result.addLast(id);
                }
            }
        } catch (JSONException ignored) {
            // A malformed, non-sensitive dedupe cache is safe to discard.
        }
        return result;
    }

    private static SharedPreferences prefs(Context context) {
        return context.getApplicationContext()
                .getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    private static boolean isEmpty(String value) {
        return value == null || value.trim().isEmpty();
    }

    private static String normalizeServer(String server) {
        if (server == null) {
            return null;
        }
        String candidate = server.trim().replaceAll("/+$", "");
        Uri uri = Uri.parse(candidate);
        String scheme = uri.getScheme();
        if (scheme == null || uri.getHost() == null
                || !(scheme.equalsIgnoreCase("http") || scheme.equalsIgnoreCase("https"))) {
            return null;
        }
        return candidate;
    }

    private static String encrypt(String clearText) throws GeneralSecurityException, JSONException {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, encryptionKey());
        byte[] ciphertext = cipher.doFinal(clearText.getBytes(StandardCharsets.UTF_8));
        return new JSONObject()
                .put("iv", Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP))
                .put("data", Base64.encodeToString(ciphertext, Base64.NO_WRAP))
                .toString();
    }

    private static String decrypt(String encoded)
            throws GeneralSecurityException, JSONException, IllegalArgumentException {
        JSONObject envelope = new JSONObject(encoded);
        byte[] iv = Base64.decode(envelope.getString("iv"), Base64.NO_WRAP);
        byte[] ciphertext = Base64.decode(envelope.getString("data"), Base64.NO_WRAP);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, encryptionKey(), new GCMParameterSpec(128, iv));
        return new String(cipher.doFinal(ciphertext), StandardCharsets.UTF_8);
    }

    private static SecretKey encryptionKey() throws GeneralSecurityException {
        KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
        try {
            keyStore.load(null);
        } catch (IOException error) {
            throw new GeneralSecurityException("Unable to load Android Keystore", error);
        }
        KeyStore.Entry existing = keyStore.getEntry(KEY_ALIAS, null);
        if (existing instanceof KeyStore.SecretKeyEntry secretKeyEntry) {
            return secretKeyEntry.getSecretKey();
        }
        KeyGenerator generator = KeyGenerator.getInstance(
                KeyProperties.KEY_ALGORITHM_AES,
                "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build());
        return generator.generateKey();
    }

    static final class Credentials {
        final String server;
        final String token;

        Credentials(String server, String token) {
            this.server = server;
            this.token = token;
        }
    }
}
