package local.codex.lanconsole;

import android.appwidget.AppWidgetManager;
import android.content.ComponentName;
import android.content.Context;
import android.os.SystemClock;
import android.widget.RemoteViews;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/** Fetches the bridge-owned quota snapshot and updates every widget instance. */
final class CodexQuotaWidgetUpdater {
    private static final int MAX_RESPONSE_BYTES = 256 * 1024;
    private static final long MIN_BACKGROUND_REFRESH_MS = 45_000L;
    private static final AtomicBoolean FETCHING = new AtomicBoolean();
    private static final AtomicLong LAST_ATTEMPT = new AtomicLong();
    private static final ExecutorService EXECUTOR = Executors.newSingleThreadExecutor(runnable -> {
        Thread thread = new Thread(runnable, "codex-quota-widget");
        thread.setDaemon(true);
        return thread;
    });
    private static final DateTimeFormatter RESET_TIME = DateTimeFormatter.ofPattern("HH:mm");

    private CodexQuotaWidgetUpdater() {}

    static void requestRefresh(Context context, boolean force, Runnable completion) {
        Context app = context.getApplicationContext();
        if (!hasWidgets(app)) {
            finish(completion);
            return;
        }
        long now = SystemClock.elapsedRealtime();
        long previous = LAST_ATTEMPT.get();
        if (!force && previous > 0L && now - previous < MIN_BACKGROUND_REFRESH_MS) {
            finish(completion);
            return;
        }
        if (!FETCHING.compareAndSet(false, true)) {
            finish(completion);
            return;
        }
        LAST_ATTEMPT.set(now);
        EXECUTOR.execute(() -> {
            try {
                NotificationConfigStore.Credentials credentials =
                        NotificationConfigStore.credentials(app);
                if (credentials == null) {
                    NotificationConfigStore.setWidgetQuotaError(app, "请先配对电脑");
                } else {
                    String payload = fetch(credentials);
                    JSONObject quota = new JSONObject(payload);
                    if (!quota.has("available")) {
                        throw new JSONException("Missing quota availability");
                    }
                    if (!NotificationConfigStore.saveWidgetQuota(app, quota.toString())) {
                        throw new IOException("Unable to save quota snapshot");
                    }
                }
            } catch (AuthenticationException error) {
                NotificationConfigStore.setWidgetQuotaError(app, "配对已失效");
            } catch (IOException | JSONException | RuntimeException error) {
                NotificationConfigStore.setWidgetQuotaError(app, "电脑暂时离线");
            } finally {
                renderCached(app);
                FETCHING.set(false);
                finish(completion);
            }
        });
    }

    static void renderCached(Context context) {
        Context app = context.getApplicationContext();
        AppWidgetManager manager = AppWidgetManager.getInstance(app);
        int[] ids = manager.getAppWidgetIds(new ComponentName(app, CodexQuotaWidgetProvider.class));
        if (ids.length == 0) {
            return;
        }
        String raw = NotificationConfigStore.widgetQuota(app);
        String error = NotificationConfigStore.widgetQuotaError(app);
        long savedAt = NotificationConfigStore.widgetQuotaSavedAt(app);
        JSONObject payload = null;
        if (!raw.isEmpty()) {
            try {
                payload = new JSONObject(raw);
            } catch (JSONException ignored) {
                // The next successful refresh replaces a malformed cache.
            }
        }
        RemoteViews views = buildViews(app, payload, savedAt, error);
        manager.updateAppWidget(ids, views);
    }

    private static RemoteViews buildViews(
            Context context,
            JSONObject payload,
            long savedAt,
            String localError) {
        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.widget_codex_quota);
        CodexQuotaWidgetProvider.attachActions(context, views);

        if (payload == null || !payload.optBoolean("available", false)) {
            views.setTextViewText(R.id.quota_title, "CODEX");
            views.setTextViewText(R.id.quota_remaining, "--%");
            views.setProgressBar(R.id.quota_progress, 100, 0, false);
            views.setTextViewText(R.id.quota_primary_estimate, "额度暂不可用");
            views.setTextViewText(R.id.quota_estimators, "近 -- · 稳 -- · 窗 --");
            views.setTextViewText(
                    R.id.quota_meta,
                    localError == null || localError.isEmpty() ? "点按刷新" : localError);
            return views;
        }

        JSONObject window = payload.optJSONObject("window");
        if (window == null) {
            views.setTextViewText(R.id.quota_remaining, "--%");
            views.setProgressBar(R.id.quota_progress, 100, 0, false);
            views.setTextViewText(R.id.quota_primary_estimate, "等待额度窗口");
            views.setTextViewText(R.id.quota_estimators, "近 -- · 稳 -- · 窗 --");
            views.setTextViewText(R.id.quota_meta, safeText(localError, "点按刷新"));
            return views;
        }

        double remaining = number(window, "remainingPercent", Double.NaN);
        if (!Double.isFinite(remaining)) {
            remaining = 100.0 - number(window, "usedPercent", 100.0);
        }
        remaining = clamp(remaining, 0.0, 100.0);
        int roundedRemaining = (int) Math.round(remaining);
        views.setTextViewText(R.id.quota_remaining, roundedRemaining + "%");
        views.setProgressBar(R.id.quota_progress, 100, roundedRemaining, false);

        String plan = payload.optString("planType", "").trim().toUpperCase(Locale.ROOT);
        views.setTextViewText(R.id.quota_title,
                plan.isEmpty() || "UNKNOWN".equals(plan) ? "CODEX" : "CODEX " + compact(plan, 8));

        JSONObject estimators = payload.optJSONObject("estimators");
        JSONObject recent = estimators == null ? null : estimators.optJSONObject("recent");
        JSONObject trend = estimators == null ? null : estimators.optJSONObject("trend");
        JSONObject windowAverage = estimators == null
                ? null
                : estimators.optJSONObject("windowAverage");
        JSONObject primary = payload.optJSONObject("primaryEstimate");
        if (primary == null) {
            primary = trend != null ? trend : recent != null ? recent : windowAverage;
        }

        String rate = rateText(primary);
        String primaryEta = etaText(primary);
        String main;
        if (warmingUp(primary)) {
            main = "正在学习用量";
        } else if (rate.isEmpty()) {
            main = "预计 " + primaryEta;
        } else {
            main = rate + " · " + (reachesReset(primary) ? "到重置" : "约 " + primaryEta);
        }
        views.setTextViewText(R.id.quota_primary_estimate, main);
        views.setTextViewText(
                R.id.quota_estimators,
                "近 " + etaText(recent) + " · 稳 " + etaText(trend) + " · 窗 " + etaText(windowAverage));

        String label = compact(window.optString("label", "额度"), 10);
        String reset = resetText(window);
        boolean stale = payload.optBoolean("stale", false)
                || (savedAt > 0L && System.currentTimeMillis() - savedAt > 35L * 60L * 1000L);
        StringBuilder meta = new StringBuilder();
        if (stale) {
            meta.append("旧 · ");
        }
        meta.append(label.isEmpty() ? "额度" : label);
        if (!reset.isEmpty()) {
            meta.append(" · ").append(reset);
        }
        if (localError != null && !localError.isEmpty()) {
            meta.append(" · 离线");
        }
        views.setTextViewText(R.id.quota_meta, compact(meta.toString(), 22));
        return views;
    }

    private static String fetch(NotificationConfigStore.Credentials credentials)
            throws IOException, AuthenticationException {
        HttpURLConnection connection = (HttpURLConnection) new URL(
                credentials.server + "/api/quota").openConnection();
        connection.setRequestMethod("GET");
        connection.setInstanceFollowRedirects(false);
        // AppWidget broadcasts have a short execution budget. The bridge endpoint is a
        // cached LAN read, so fail quickly and keep displaying the last good snapshot.
        connection.setConnectTimeout(4_000);
        connection.setReadTimeout(5_000);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("Authorization", "Bearer " + credentials.token);
        connection.setRequestProperty("Cache-Control", "no-store");
        try {
            int status = connection.getResponseCode();
            if (status == HttpURLConnection.HTTP_UNAUTHORIZED
                    || status == HttpURLConnection.HTTP_FORBIDDEN) {
                throw new AuthenticationException();
            }
            if (status != HttpURLConnection.HTTP_OK) {
                throw new IOException("Unexpected HTTP status " + status);
            }
            return readLimited(connection.getInputStream());
        } finally {
            connection.disconnect();
        }
    }

    private static String readLimited(InputStream input) throws IOException {
        try (InputStream stream = input; ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8 * 1024];
            int total = 0;
            int read;
            while ((read = stream.read(buffer)) >= 0) {
                total += read;
                if (total > MAX_RESPONSE_BYTES) {
                    throw new IOException("Quota response is too large");
                }
                output.write(buffer, 0, read);
            }
            return new String(output.toByteArray(), StandardCharsets.UTF_8);
        }
    }

    private static boolean hasWidgets(Context context) {
        int[] ids = AppWidgetManager.getInstance(context).getAppWidgetIds(
                new ComponentName(context, CodexQuotaWidgetProvider.class));
        return ids.length > 0;
    }

    private static double number(JSONObject value, String name, double fallback) {
        if (value == null || value.isNull(name)) {
            return fallback;
        }
        double result = value.optDouble(name, fallback);
        return Double.isFinite(result) ? result : fallback;
    }

    private static String rateText(JSONObject estimate) {
        if (warmingUp(estimate)) {
            return "";
        }
        double rate = number(estimate, "ratePercentPerHour", Double.NaN);
        if (!Double.isFinite(rate) || rate < 0.0) {
            return "";
        }
        if (rate < 0.05) {
            return "0%/h";
        }
        return String.format(Locale.US, rate < 10.0 ? "%.1f%%/h" : "%.0f%%/h", rate);
    }

    private static String etaText(JSONObject estimate) {
        if (estimate == null) {
            return "--";
        }
        if (warmingUp(estimate)) {
            return "学习中";
        }
        if (reachesReset(estimate)) {
            return "重置";
        }
        double seconds = number(estimate, "etaSeconds", Double.NaN);
        if (!Double.isFinite(seconds) || seconds < 0.0) {
            return "--";
        }
        if (seconds < 60.0) {
            return "<1m";
        }
        double minutes = seconds / 60.0;
        if (minutes < 60.0) {
            return Math.max(1L, Math.round(minutes)) + "m";
        }
        double hours = minutes / 60.0;
        if (hours < 48.0) {
            return hours < 10.0
                    ? String.format(Locale.US, "%.1fh", hours)
                    : Math.round(hours) + "h";
        }
        double days = hours / 24.0;
        return days < 10.0
                ? String.format(Locale.US, "%.1fd", days)
                : Math.round(days) + "d";
    }

    private static boolean reachesReset(JSONObject estimate) {
        return estimate != null && estimate.optBoolean("reachesReset", false);
    }

    private static boolean warmingUp(JSONObject estimate) {
        if (estimate == null) {
            return false;
        }
        if (estimate.optBoolean("warmingUp", false)) {
            return true;
        }
        String confidence = estimate.optString("confidence", "");
        return "warmingup".equalsIgnoreCase(confidence.replaceAll("[^A-Za-z]", ""));
    }

    private static String resetText(JSONObject window) {
        Object raw = window.opt("resetsAt");
        if (raw == null || raw == JSONObject.NULL) {
            return "";
        }
        try {
            Instant instant;
            if (raw instanceof Number number) {
                instant = Instant.ofEpochSecond(number.longValue());
            } else {
                String text = String.valueOf(raw).trim();
                instant = text.matches("[0-9]{1,19}")
                        ? Instant.ofEpochSecond(Long.parseLong(text))
                        : OffsetDateTime.parse(text).toInstant();
            }
            return RESET_TIME.format(instant.atZone(ZoneId.systemDefault())) + "重置";
        } catch (RuntimeException error) {
            return "";
        }
    }

    private static double clamp(double value, double minimum, double maximum) {
        return Math.max(minimum, Math.min(maximum, value));
    }

    private static String compact(String value, int maximum) {
        String cleaned = value == null ? "" : value.trim().replaceAll("\\s+", " ");
        if (cleaned.length() <= maximum) {
            return cleaned;
        }
        return cleaned.substring(0, Math.max(0, maximum - 1)) + "…";
    }

    private static String safeText(String value, String fallback) {
        return value == null || value.trim().isEmpty() ? fallback : value.trim();
    }

    private static void finish(Runnable completion) {
        if (completion != null) {
            try {
                completion.run();
            } catch (RuntimeException ignored) {
                // A receiver may already have been reclaimed; the widget update still stands.
            }
        }
    }

    private static final class AuthenticationException extends Exception {}
}
