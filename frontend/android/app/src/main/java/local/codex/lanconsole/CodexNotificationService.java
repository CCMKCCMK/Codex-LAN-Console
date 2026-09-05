package local.codex.lanconsole;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.graphics.Color;
import android.media.AudioAttributes;
import android.media.RingtoneManager;
import android.net.Uri;
import android.os.Build;
import android.os.IBinder;
import android.os.SystemClock;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * An explicitly enabled foreground service that long-polls the paired desktop.
 * The ongoing notification makes Android's background work visible and stoppable.
 */
public final class CodexNotificationService extends Service {
    static final String ACTION_START = "local.codex.lanconsole.notifications.START";
    static final String ACTION_STOP = "local.codex.lanconsole.notifications.STOP";
    static final String ACTION_TEST = "local.codex.lanconsole.notifications.TEST";
    static final String ACTION_RELOAD = "local.codex.lanconsole.notifications.RELOAD";
    static final String ACTION_STATUS = "local.codex.lanconsole.notifications.STATUS";
    static final String EXTRA_THREAD_ID = "thread_id";
    static final String EXTRA_OPEN_CONSOLE = "open_console";
    private static final String EXTRA_DISABLE = "disable";

    private static final String MONITOR_CHANNEL = "codex_monitor_v1";
    private static final String ALERT_CHANNEL = "codex_task_alerts_v1";
    private static final int FOREGROUND_ID = 41001;
    private static final int MAX_RESPONSE_BYTES = 1024 * 1024;
    private static final long FAST_EMPTY_RESPONSE_MS = 5_000L;
    private static final long FAST_EMPTY_BACKOFF_MS = 10_000L;

    private static volatile boolean running;

    private final Object workerLock = new Object();
    private volatile WorkerSession currentWorker;
    private volatile boolean stoppingService;

    static boolean isRunning() {
        return running;
    }

    static boolean hasNotificationPermission(Context context) {
        boolean runtimeGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU
                || context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                == PackageManager.PERMISSION_GRANTED;
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        return runtimeGranted
                && (manager == null || manager.areNotificationsEnabled())
                && alertChannelEnabled(context);
    }

    static boolean alertChannelEnabled(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        NotificationChannel channel = manager == null
                ? null
                : manager.getNotificationChannel(ALERT_CHANNEL);
        return channel == null || channel.getImportance() != NotificationManager.IMPORTANCE_NONE;
    }

    static boolean start(Context context) {
        if (!NotificationConfigStore.isEnabled(context)
                || !NotificationConfigStore.isConfigured(context)
                || !hasNotificationPermission(context)) {
            return false;
        }
        try {
            Intent intent = new Intent(context, CodexNotificationService.class)
                    .setAction(ACTION_START);
            context.startForegroundService(intent);
            return true;
        } catch (RuntimeException error) {
            NotificationConfigStore.setLastError(
                    context,
                    "Android 暂时不允许从后台启动监听，请打开应用后重试");
            broadcastStatus(context);
            return false;
        }
    }

    static void stop(Context context, boolean disable) {
        if (disable) {
            NotificationConfigStore.setEnabled(context, false);
            NotificationConfigStore.setLastError(context, "");
        }
        try {
            context.startService(new Intent(context, CodexNotificationService.class)
                    .setAction(ACTION_STOP)
                    .putExtra(EXTRA_DISABLE, disable));
        } catch (RuntimeException ignored) {
            context.stopService(new Intent(context, CodexNotificationService.class));
        }
        broadcastStatus(context);
    }

    static void reload(Context context) {
        if (!isRunning()) {
            start(context);
            return;
        }
        try {
            context.startForegroundService(new Intent(context, CodexNotificationService.class)
                    .setAction(ACTION_RELOAD));
        } catch (RuntimeException error) {
            NotificationConfigStore.setLastError(
                    context,
                    "后台连接切换失败，请重新打开应用后再试");
            broadcastStatus(context);
        }
    }

    static void test(Context context) {
        try {
            context.startService(new Intent(context, CodexNotificationService.class)
                    .setAction(ACTION_TEST));
        } catch (RuntimeException ignored) {
            // The UI reports service state; a stopped service cannot run a background test.
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        createChannels();
        running = true;
        broadcastStatus(this);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? ACTION_START : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            if (intent == null || intent.getBooleanExtra(EXTRA_DISABLE, true)) {
                NotificationConfigStore.setEnabled(this, false);
                NotificationConfigStore.setLastError(this, "");
            }
            stopMonitor();
            return START_NOT_STICKY;
        }
        if (ACTION_TEST.equals(action)) {
            if (hasNotificationPermission(this)) {
                postAlert(
                        "test-" + System.currentTimeMillis(),
                        "通知测试成功",
                        "Codex 后台任务完成或需要你决定时，手机会这样提醒。",
                        null,
                        true);
            }
            if (!NotificationConfigStore.isEnabled(this)) {
                stopMonitor();
                return START_NOT_STICKY;
            }
        }

        if (!NotificationConfigStore.isEnabled(this)
                || !NotificationConfigStore.isConfigured(this)
                || !hasNotificationPermission(this)) {
            stopMonitor();
            return START_NOT_STICKY;
        }

        startInForeground();
        startWorkerIfNeeded(ACTION_RELOAD.equals(action));
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        stoppingService = true;
        cancelCurrentWorker();
        running = false;
        broadcastStatus(this);
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void startInForeground() {
        Notification notification = monitorNotification("正在后台监听远程 Codex");
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                    FOREGROUND_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_REMOTE_MESSAGING);
        } else {
            startForeground(FOREGROUND_ID, notification);
        }
    }

    private void startWorkerIfNeeded(boolean forceReload) {
        synchronized (workerLock) {
            long generation = NotificationConfigStore.generation(this);
            WorkerSession existing = currentWorker;
            if (!forceReload && existing != null
                    && !existing.cancelled.get()
                    && existing.generation == generation) {
                return;
            }
            cancelWorker(existing);
            stoppingService = false;
            WorkerSession replacement = new WorkerSession(generation);
            currentWorker = replacement;
            replacement.executor.execute(() -> monitorLoop(replacement));
        }
    }

    private void monitorLoop(WorkerSession session) {
        long retryDelayMs = 2_000L;
        while (isCurrent(session) && NotificationConfigStore.isEnabled(this)) {
            if (!hasNotificationPermission(this)) {
                NotificationConfigStore.setLastError(this, "系统通知已关闭，请在设置中允许 Codex 任务提醒");
                broadcastStatus(this);
                break;
            }
            NotificationConfigStore.Credentials credentials =
                    NotificationConfigStore.credentials(this);
            if (credentials == null) {
                if (isCurrent(session)) {
                    NotificationConfigStore.setEnabled(this, false);
                }
                break;
            }
            long startedAt = SystemClock.elapsedRealtime();
            try {
                FeedResult result = fetchEvents(session, credentials);
                if (!isCurrent(session)) {
                    break;
                }
                retryDelayMs = 2_000L;
                if (!processFeed(session, result)) {
                    if (!isCurrent(session)) {
                        break;
                    }
                    NotificationConfigStore.setLastError(
                            this,
                            "通知暂时未能送达，后台正在重试");
                    broadcastStatus(this);
                    interruptibleSleep(session, retryDelayMs);
                    retryDelayMs = Math.min(60_000L, retryDelayMs * 2L);
                    continue;
                }
                NotificationConfigStore.setLastError(this, "");
                if (!isCurrent(session)) {
                    break;
                }
                // The widget updater is internally throttled and returns immediately. Calling
                // it after each successful long poll keeps visible widgets close to live data
                // without adding a second permanent background service.
                CodexQuotaWidgetUpdater.requestRefresh(this, false, null);
                updateMonitorNotification("已连接，正在等待 Codex 任务状态");
                broadcastStatus(this);

                long duration = SystemClock.elapsedRealtime() - startedAt;
                if (result.events.length() == 0 && duration < FAST_EMPTY_RESPONSE_MS) {
                    interruptibleSleep(session, FAST_EMPTY_BACKOFF_MS);
                }
            } catch (AuthenticationException error) {
                if (!isCurrent(session)) {
                    break;
                }
                NotificationConfigStore.setLastError(this, "配对已失效，请在应用内重新启用通知");
                NotificationConfigStore.setEnabled(this, false);
                broadcastStatus(this);
                break;
            } catch (IOException | JSONException error) {
                if (!isCurrent(session)) {
                    break;
                }
                NotificationConfigStore.setLastError(this, "电脑暂时离线，后台正在重连");
                updateMonitorNotification("电脑暂时离线，正在重连");
                broadcastStatus(this);
                interruptibleSleep(session, retryDelayMs);
                retryDelayMs = Math.min(60_000L, retryDelayMs * 2L);
            } catch (RuntimeException error) {
                if (!isCurrent(session)) {
                    break;
                }
                NotificationConfigStore.setLastError(this, "后台监听遇到错误，正在重试");
                broadcastStatus(this);
                interruptibleSleep(session, retryDelayMs);
                retryDelayMs = Math.min(60_000L, retryDelayMs * 2L);
            }
        }
        boolean ownsService;
        synchronized (workerLock) {
            ownsService = currentWorker == session;
            if (ownsService) {
                currentWorker = null;
            }
        }
        session.executor.shutdown();
        if (ownsService && !stoppingService) {
            stopSelf();
        }
    }

    private FeedResult fetchEvents(
            WorkerSession session,
            NotificationConfigStore.Credentials credentials)
            throws IOException, JSONException, AuthenticationException {
        String cursor = NotificationConfigStore.cursor(this);
        Uri.Builder uri = Uri.parse(credentials.server + "/api/notifications/events")
                .buildUpon()
                .appendQueryParameter("limit", "100");
        if (!isEmpty(cursor)) {
            uri.appendQueryParameter("after", cursor)
                    .appendQueryParameter("wait", "25");
        }

        HttpURLConnection connection = (HttpURLConnection) new URL(uri.build().toString())
                .openConnection();
        session.connection = connection;
        connection.setRequestMethod("GET");
        connection.setInstanceFollowRedirects(false);
        connection.setConnectTimeout(12_000);
        connection.setReadTimeout(35_000);
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
            JSONObject payload = new JSONObject(readLimited(connection.getInputStream()));
            JSONArray events = payload.optJSONArray("events");
            if (events == null) {
                events = new JSONArray();
            }
            String nextCursor = jsonScalar(payload, "nextCursor");
            String currentCursor = jsonScalar(payload, "currentCursor");
            if (!nextCursor.matches("-?[0-9]{1,19}")
                    || !currentCursor.matches("[0-9]{1,19}")) {
                throw new JSONException("Invalid notification cursor");
            }
            return new FeedResult(
                    events,
                    isEmpty(nextCursor) ? currentCursor : nextCursor,
                    currentCursor,
                    payload.optBoolean("hasMore", false),
                    payload.optBoolean("cursorExpired", false));
        } finally {
            session.connection = null;
            connection.disconnect();
        }
    }

    private boolean processFeed(WorkerSession session, FeedResult feed) {
        String localCursor = NotificationConfigStore.cursor(this);
        boolean bootstrap = isEmpty(localCursor)
                || localCursor.startsWith("-");

        // A rebuilt journal can reuse numeric event ids. Its old dedupe set is invalid.
        if (feed.cursorExpired
                && !NotificationConfigStore.clearSeenEvents(this, session.generation)) {
            return false;
        }

        for (int index = 0; index < feed.events.length(); index++) {
            if (!isCurrent(session)) {
                return false;
            }
            JSONObject event = feed.events.optJSONObject(index);
            if (event == null) {
                continue;
            }
            String id = jsonScalar(event, "id");
            String type = event.optString("type", "action_required");
            boolean requiresAction = event.optBoolean("requiresAction", isActionType(type));
            // First-enable bootstrap pages may contain historical terminal states. Only
            // unresolved actions are relevant until the temporary negative cursor is drained.
            if (bootstrap && !requiresAction) {
                continue;
            }
            if (NotificationConfigStore.hasSeenEvent(this, id)) {
                continue;
            }
            String title = cleanText(event.optString("title", defaultTitle(type)), 100);
            String body = cleanText(event.optString("body", defaultBody(type)), 240);
            String threadId = cleanIdentifier(event.optString("threadId", ""));
            if (!postAlert(id, title, body, threadId, requiresAction)) {
                return false;
            }
            // Persist dedupe only after NotificationManager accepted the notification.
            if (!NotificationConfigStore.rememberEvent(this, id, session.generation)) {
                return false;
            }
        }
        // nextCursor is authoritative, including negative bootstrap pagination cursors.
        return isCurrent(session)
                && NotificationConfigStore.setCursor(
                        this,
                        feed.nextCursor,
                        session.generation);
    }

    private boolean postAlert(
            String eventId,
            String title,
            String body,
            String threadId,
            boolean requiresAction) {
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager == null || !hasNotificationPermission(this)) {
            return false;
        }

        Intent open = new Intent(this, MainActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(EXTRA_OPEN_CONSOLE, true);
        if (!isEmpty(threadId)) {
            open.putExtra(EXTRA_THREAD_ID, threadId);
        }
        int requestCode = stableNotificationId(eventId);
        PendingIntent contentIntent = PendingIntent.getActivity(
                this,
                requestCode,
                open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification.Builder builder = new Notification.Builder(this, ALERT_CHANNEL)
                .setSmallIcon(R.drawable.ic_notification)
                .setColor(Color.rgb(123, 241, 189))
                .setContentTitle(title)
                .setContentText(body)
                .setStyle(new Notification.BigTextStyle().bigText(body))
                .setContentIntent(contentIntent)
                .setAutoCancel(true)
                .setOnlyAlertOnce(true)
                .setVisibility(Notification.VISIBILITY_PRIVATE)
                .setCategory(requiresAction
                        ? Notification.CATEGORY_REMINDER
                        : Notification.CATEGORY_STATUS)
                .setPriority(requiresAction
                        ? Notification.PRIORITY_HIGH
                        : Notification.PRIORITY_DEFAULT);
        builder.setPublicVersion(new Notification.Builder(this, ALERT_CHANNEL)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle("Codex 任务提醒")
                .setContentText("解锁手机后查看详情")
                .build());
        try {
            manager.notify(requestCode, builder.build());
            return true;
        } catch (RuntimeException error) {
            return false;
        }
    }

    private Notification monitorNotification(String status) {
        Intent open = new Intent(this, MainActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(EXTRA_OPEN_CONSOLE, true);
        PendingIntent contentIntent = PendingIntent.getActivity(
                this,
                0,
                open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stopIntent = PendingIntent.getService(
                this,
                1,
                new Intent(this, CodexNotificationService.class)
                        .setAction(ACTION_STOP)
                        .putExtra(EXTRA_DISABLE, true),
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, MONITOR_CHANNEL)
                .setSmallIcon(R.drawable.ic_notification)
                .setColor(Color.rgb(123, 241, 189))
                .setContentTitle("Codex 后台提醒已开启")
                .setContentText(status)
                .setContentIntent(contentIntent)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setCategory(Notification.CATEGORY_SERVICE)
                .setVisibility(Notification.VISIBILITY_PRIVATE)
                .addAction(new Notification.Action.Builder(
                        R.drawable.ic_notification,
                        "停止提醒",
                        stopIntent).build())
                .build();
    }

    private void updateMonitorNotification(String status) {
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager != null) {
            manager.notify(FOREGROUND_ID, monitorNotification(status));
        }
    }

    private void createChannels() {
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager == null) {
            return;
        }
        NotificationChannel monitor = new NotificationChannel(
                MONITOR_CHANNEL,
                "后台连接状态",
                NotificationManager.IMPORTANCE_LOW);
        monitor.setDescription("保持与已配对电脑的 Codex 任务连接");
        monitor.setSound(null, null);
        monitor.enableVibration(false);
        monitor.setShowBadge(false);
        monitor.setLockscreenVisibility(Notification.VISIBILITY_PRIVATE);
        manager.createNotificationChannel(monitor);

        NotificationChannel alerts = new NotificationChannel(
                ALERT_CHANNEL,
                "Codex 任务提醒",
                NotificationManager.IMPORTANCE_HIGH);
        alerts.setDescription("任务完成以及需要批准、输入或决定时发出提醒");
        Uri sound = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_NOTIFICATION);
        AudioAttributes audio = new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_NOTIFICATION_EVENT)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build();
        alerts.setSound(sound, audio);
        alerts.enableVibration(true);
        alerts.setLockscreenVisibility(Notification.VISIBILITY_PRIVATE);
        manager.createNotificationChannel(alerts);
    }

    private void stopMonitor() {
        stoppingService = true;
        cancelCurrentWorker();
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    private void interruptibleSleep(WorkerSession session, long milliseconds) {
        long remaining = milliseconds;
        while (isCurrent(session) && remaining > 0L) {
            long slice = Math.min(remaining, 1_000L);
            SystemClock.sleep(slice);
            remaining -= slice;
        }
    }

    private boolean isCurrent(WorkerSession session) {
        return session != null
                && !session.cancelled.get()
                && currentWorker == session
                && NotificationConfigStore.generation(this) == session.generation;
    }

    private void cancelCurrentWorker() {
        synchronized (workerLock) {
            WorkerSession session = currentWorker;
            currentWorker = null;
            cancelWorker(session);
        }
    }

    private static void cancelWorker(WorkerSession session) {
        if (session == null) {
            return;
        }
        session.cancelled.set(true);
        HttpURLConnection connection = session.connection;
        if (connection != null) {
            connection.disconnect();
        }
        session.executor.shutdownNow();
    }

    private static String readLimited(InputStream input) throws IOException {
        try (InputStream source = input; ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int total = 0;
            int read;
            while ((read = source.read(buffer)) >= 0) {
                total += read;
                if (total > MAX_RESPONSE_BYTES) {
                    throw new IOException("Notification response is too large");
                }
                output.write(buffer, 0, read);
            }
            return new String(output.toByteArray(), StandardCharsets.UTF_8);
        }
    }

    private static String jsonScalar(JSONObject object, String key) {
        Object value = object.opt(key);
        if (value == null || value == JSONObject.NULL) {
            return "";
        }
        return String.valueOf(value);
    }

    private static boolean isEmpty(String value) {
        return value == null || value.trim().isEmpty();
    }

    private static String cleanText(String value, int limit) {
        if (value == null) {
            return "";
        }
        String cleaned = value.replaceAll("[\\p{Cntrl}&&[^\\n\\t]]", " ")
                .replaceAll("\\s+", " ")
                .trim();
        return cleaned.length() <= limit ? cleaned : cleaned.substring(0, limit - 1) + "…";
    }

    private static String cleanIdentifier(String value) {
        if (value == null || value.length() > 200
                || !value.matches("[A-Za-z0-9._:/\\-]+")) {
            return "";
        }
        return value;
    }

    private static boolean isActionType(String type) {
        String normalized = type == null ? "" : type.toLowerCase(Locale.ROOT);
        return normalized.endsWith("_required")
                || normalized.equals("input_required")
                || normalized.equals("decision_required")
                || normalized.equals("approval_required");
    }

    private static String defaultTitle(String type) {
        return switch (type == null ? "" : type.toLowerCase(Locale.ROOT)) {
            case "task_completed" -> "Codex 任务已完成";
            case "task_failed" -> "Codex 任务未能完成";
            case "task_stopped" -> "Codex 任务已停止";
            case "approval_required" -> "Codex 等待你的批准";
            case "input_required" -> "Codex 等待你的输入";
            case "decision_required" -> "Codex 等待你的决定";
            default -> "Codex 需要你处理";
        };
    }

    private static String defaultBody(String type) {
        return isActionType(type)
                ? "点按通知打开对应任务并继续处理。"
                : "点按通知查看任务结果。";
    }

    private static int stableNotificationId(String eventId) {
        int hash = eventId == null ? 0 : eventId.hashCode();
        return 50_000 + (hash & 0x3fffffff);
    }

    private static void broadcastStatus(Context context) {
        context.sendBroadcast(new Intent(ACTION_STATUS)
                .setPackage(context.getPackageName()));
    }

    private static final class WorkerSession {
        final long generation;
        final AtomicBoolean cancelled = new AtomicBoolean();
        final ExecutorService executor;
        volatile HttpURLConnection connection;

        WorkerSession(long generation) {
            this.generation = generation;
            this.executor = Executors.newSingleThreadExecutor(runnable -> {
                Thread thread = new Thread(
                        runnable,
                        "codex-notification-monitor-" + generation);
                thread.setDaemon(true);
                return thread;
            });
        }
    }

    private static final class FeedResult {
        final JSONArray events;
        final String nextCursor;
        final String currentCursor;
        final boolean hasMore;
        final boolean cursorExpired;

        FeedResult(
                JSONArray events,
                String nextCursor,
                String currentCursor,
                boolean hasMore,
                boolean cursorExpired) {
            this.events = events;
            this.nextCursor = nextCursor;
            this.currentCursor = currentCursor;
            this.hasMore = hasMore;
            this.cursorExpired = cursorExpired;
        }
    }

    private static final class AuthenticationException extends Exception {}
}
