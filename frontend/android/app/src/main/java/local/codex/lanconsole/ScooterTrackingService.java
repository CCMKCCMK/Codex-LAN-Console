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
import android.location.Location;
import android.location.LocationListener;
import android.location.LocationManager;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.AtomicFile;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.io.FileOutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** User-started location FGS. No boot tracking, no hidden location collection. */
public final class ScooterTrackingService extends Service implements LocationListener {
    private static final int ONGOING = 42001, ALERT = 42002;
    private static final String CHANNEL = "scooter_tracking_v1", WARNINGS = "scooter_return_v1";
    private static final String STOP = "scooter.stop";
    private static volatile boolean running;
    private static volatile String message = "Android 后台定位可用；点击开始后才采集。";
    private static volatile int pending;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private LocationManager locations;
    private String rideId, origin;
    private JSONArray queue = new JSONArray();
    private long sequence = System.currentTimeMillis(), lastUpload, lastAlert;
    private boolean stopping;
    private AtomicFile file;
    private final Runnable tick = new Runnable() {
        public void run() { if (!stopping) { worker.execute(() -> flush()); handler.postDelayed(this, 15000); } }
    };

    static String status() {
        try { return new JSONObject().put("running", running).put("pending", pending).put("message", message).toString(); }
        catch (Exception e) { return "{}"; }
    }
    static void problem(String text) { message = text; }
    static void start(Context context, String id) {
        context.startForegroundService(new Intent(context, ScooterTrackingService.class).putExtra("ride", id));
    }
    static void stop(Context context) {
        if (running) context.startService(new Intent(context, ScooterTrackingService.class).setAction(STOP));
    }
    @Override public void onCreate() {
        super.onCreate();
        file = new AtomicFile(new File(getFilesDir(), "scooter-pending.json"));
        try { if (file.getBaseFile().exists()) queue = new JSONArray(new String(file.readFully(), StandardCharsets.UTF_8)); }
        catch (Exception e) { message = "离线骑行文件读取失败，已保留原文件，请勿继续记录。"; stopping = true; }
        pending = queue.length();
        locations = getSystemService(LocationManager.class);
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Scooter 骑行记录", NotificationManager.IMPORTANCE_LOW));
        nm.createNotificationChannel(new NotificationChannel(WARNINGS, "Scooter 返程电量提醒", NotificationManager.IMPORTANCE_HIGH));
    }
    private Notification notification(String body) {
        Intent open = new Intent(this, MainActivity.class).putExtra(CodexNotificationService.EXTRA_OPEN_CONSOLE, true)
                .putExtra(CodexNotificationService.EXTRA_THREAD_ID, "commute");
        PendingIntent content = PendingIntent.getActivity(this, ONGOING, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stop = PendingIntent.getService(this, ONGOING, new Intent(this, ScooterTrackingService.class).setAction(STOP), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_launcher).setContentTitle("Scooter · 正在记录骑行")
                .setContentText(body).setContentIntent(content).setOngoing(true).setOnlyAlertOnce(true)
                .addAction(new Notification.Action.Builder(null, "停止定位", stop).build()).build();
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && STOP.equals(intent.getAction())) { finish(); return START_NOT_STICKY; }
        if (stopping || intent == null) { stopSelf(); return START_NOT_STICKY; }
        String id = intent.getStringExtra("ride");
        NotificationConfigStore.Credentials credentials = NotificationConfigStore.credentials(this);
        if (id == null || !id.matches("[0-9a-f]{32}") || credentials == null
                || checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
            message = "请先配对并允许精确定位，然后重新点击开始。"; stopSelf(); return START_NOT_STICKY;
        }
        if (running && !id.equals(rideId)) { message = "请先停止上一段骑行定位。"; return START_NOT_STICKY; }
        rideId = id; origin = credentials.server;
        try {
            startForeground(ONGOING, notification("GPS 定位中；切到后台或锁屏后继续记录"));
            locations.removeUpdates(this);
            if (locations.isProviderEnabled(LocationManager.GPS_PROVIDER)) locations.requestLocationUpdates(LocationManager.GPS_PROVIDER, 5000, 3, this);
            else { message = "系统 GPS 未开启，请打开定位后重新开始。"; stopSelf(); return START_NOT_STICKY; }
            running = true; message = "Android 后台定位中 · 锁屏后继续记录";
            handler.removeCallbacks(tick); handler.post(tick);
        } catch (RuntimeException ex) { message = "系统未允许启动后台骑行记录，请回到 App 后重试。"; stopSelf(); }
        return START_NOT_STICKY;
    }
    @Override public void onLocationChanged(Location location) {
        if (stopping || !running) return;
        final String id = rideId, server = origin;
        worker.execute(() -> {
            if (queue.length() >= 20000) { message = "离线队列已满，请联网同步；新定位未保存。"; handler.post(this::finish); return; }
            try {
                JSONObject p = new JSONObject().put("rideId", id).put("server", server)
                        .put("seq", sequence++).put("at", Instant.ofEpochMilli(location.getTime()).toString())
                        .put("lat", location.getLatitude()).put("lon", location.getLongitude()).put("accuracy", location.getAccuracy());
                queue.put(p); persist();
                if (System.currentTimeMillis() - lastUpload > 10000) flush();
            } catch (Exception e) { message = "定位保存失败，已停止采集，请检查手机存储。"; handler.post(this::finish); }
        });
    }
    private void persist() throws Exception {
        FileOutputStream out = file.startWrite();
        try { out.write(queue.toString().getBytes(StandardCharsets.UTF_8)); file.finishWrite(out); }
        catch (Exception ex) { file.failWrite(out); throw ex; }
        pending = queue.length();
    }
    private void flush() {
        if (queue.length() == 0) return;
        lastUpload = System.currentTimeMillis();
        HttpURLConnection connection = null;
        try {
            NotificationConfigStore.Credentials credentials = NotificationConfigStore.credentials(this);
            JSONObject first = queue.getJSONObject(0);
            // Never send an old ride's coordinates to a newly paired computer.
            if (credentials == null || !credentials.server.equals(first.getString("server"))) { message = "离线记录属于另一台电脑，请重新连接原电脑后同步。"; return; }
            JSONArray batch = new JSONArray();
            boolean stopRecord = "stop".equals(first.optString("kind"));
            for (int i = 0; i < Math.min(100, queue.length()); i++) {
                if (stopRecord) break;
                JSONObject item = queue.getJSONObject(i);
                if (item.has("kind")) break;
                if (!item.getString("rideId").equals(first.getString("rideId")) || !item.getString("server").equals(credentials.server)) break;
                JSONObject point = new JSONObject(item.toString()); point.remove("rideId"); point.remove("server"); batch.put(point);
            }
            JSONObject payload = stopRecord ? new JSONObject().put("action", "stop").put("requestId", first.getString("requestId"))
                    .put("rideId", first.getString("rideId")).put("at", first.getString("at"))
                    : new JSONObject().put("rideId", first.getString("rideId")).put("points", batch);
            byte[] bytes = payload.toString().getBytes(StandardCharsets.UTF_8);
            connection = (HttpURLConnection) new URL(credentials.server + "/api/commute/scooter/" + (stopRecord ? "action" : "points")).openConnection();
            connection.setInstanceFollowRedirects(false); connection.setConnectTimeout(10000); connection.setReadTimeout(55000);
            connection.setRequestMethod("POST"); connection.setDoOutput(true); connection.setFixedLengthStreamingMode(bytes.length);
            connection.setRequestProperty("Content-Type", "application/json"); connection.setRequestProperty("Authorization", "Bearer " + credentials.token);
            try (java.io.OutputStream out = connection.getOutputStream()) { out.write(bytes); }
            if (connection.getResponseCode() != 200) throw new java.io.IOException("HTTP " + connection.getResponseCode());
            byte[] response;
            try (java.io.InputStream in = connection.getInputStream(); java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream()) {
                byte[] buffer = new byte[8192]; int n;
                while ((n = in.read(buffer)) != -1) { if (out.size() + n > 4 * 1024 * 1024) throw new java.io.IOException("Response too large"); out.write(buffer, 0, n); }
                response = out.toByteArray();
            }
            JSONObject snapshot = new JSONObject(new String(response, StandardCharsets.UTF_8));
            JSONArray remaining = new JSONArray(); for (int i = stopRecord ? 1 : batch.length(); i < queue.length(); i++) remaining.put(queue.get(i));
            queue = remaining; persist();
            message = (running ? "Android 后台定位中" : "定位已停止") + " · 待同步 " + pending + " 点";
            JSONObject estimate = snapshot.getJSONObject("estimate"), settings = snapshot.getJSONObject("data").getJSONObject("settings");
            if (running && estimate.optBoolean("returnAtRisk", false) && settings.optBoolean("alertsEnabled", true)
                    && System.currentTimeMillis() - lastAlert >= settings.optInt("alertSeconds", 60) * 1000L
                    && !CodexNotificationService.isRunning()) {
                lastAlert = System.currentTimeMillis();
                getSystemService(NotificationManager.class).notify(ALERT, new Notification.Builder(this, WARNINGS)
                        .setSmallIcon(R.drawable.ic_launcher).setContentTitle("Scooter · 请预留返程电量")
                        .setContentText(estimate.optString("message")).setStyle(new Notification.BigTextStyle().bigText(estimate.optString("message"))).setAutoCancel(true).build());
            }
        } catch (Exception ex) { message = "离线记录中 · " + pending + " 点待同步；实时返程预估暂不可用"; }
        finally { if (connection != null) connection.disconnect(); }
        if (running) getSystemService(NotificationManager.class).notify(ONGOING, notification(message));
    }
    private void finish() {
        if (stopping) return; stopping = true; running = false;
        handler.removeCallbacks(tick); if (locations != null) locations.removeUpdates(this);
        message = "定位已停止，正在同步最后的路段";
        final String at = Instant.now().toString();
        worker.execute(() -> {
            try {
                queue.put(new JSONObject().put("kind", "stop").put("rideId", rideId).put("server", origin)
                        .put("requestId", java.util.UUID.randomUUID().toString()).put("at", at));
                persist();
                for (int i = 0; i < 25 && queue.length() > 0; i++) { int before = queue.length(); flush(); if (queue.length() >= before) break; }
            } catch (Exception ex) { message = "停止记录待同步，请下次打开时保持联网。"; }
            stopForeground(STOP_FOREGROUND_REMOVE); stopSelf();
        });
    }
    @Override public void onDestroy() {
        running = false; handler.removeCallbacks(tick); if (locations != null) locations.removeUpdates(this);
        worker.shutdown(); super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
