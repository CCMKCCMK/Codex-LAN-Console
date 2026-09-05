package local.codex.lanconsole;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.ActivityManager;
import android.app.AlertDialog;
import android.app.DownloadManager;
import android.app.NotificationManager;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.database.Cursor;
import android.graphics.Color;
import android.media.MediaScannerConnection;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.MediaStore;
import android.provider.Settings;
import android.util.Base64;
import android.view.Gravity;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.MimeTypeMap;
import android.webkit.URLUtil;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;
import android.window.OnBackInvokedDispatcher;

import org.json.JSONObject;

import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

public class MainActivity extends Activity {
    private static final int STORAGE_PERMISSION_REQUEST = 7001;
    private static final int FILE_CHOOSER_REQUEST = 7002;
    private static final int NOTIFICATION_PERMISSION_REQUEST = 7003;
    private static final int SCOOTER_PERMISSION_REQUEST = 7004;
    private String pendingScooterRideId;
    private static final long MAX_BLOB_BYTES = 512L * 1024L * 1024L;
    private static final long CAPTURE_DELETE_DELAY_MS = 60L * 60L * 1000L;
    private static final long CAPTURE_STALE_AFTER_MS = 24L * 60L * 60L * 1000L;
    private static final long CAPTURE_SWEEP_INTERVAL_MS = 6L * 60L * 60L * 1000L;
    private static final String APK_MIME = "application/vnd.android.package-archive";
    private static final String STATE_WEB_ACTIVE = "web_active";
    private static final String STATE_WEB_VIEW = "web_view";
    private static final String STATE_PENDING_NOTIFICATION_THREAD =
            "pending_notification_thread";
    private static final String STATE_PENDING_NOTIFICATION_AT = "pending_notification_at";
    private static final long NOTIFICATION_ROUTE_TTL_MS = 60000L;
    private static final Handler DELAYED_FILE_CLEANUP = new Handler(Looper.getMainLooper());

    private final Map<Long, DownloadRecord> downloads = new ConcurrentHashMap<>();
    private final ArrayList<CaptureTarget> captureTargets = new ArrayList<>();
    private final BlobDownloadBridge blobBridge = new BlobDownloadBridge();
    private final NotificationBridge notificationBridge = new NotificationBridge();

    private WebView web;
    private volatile String server;
    private volatile boolean trustedConsoleOrigin;
    private volatile boolean consoleMainFrameLoadFailed;
    private volatile boolean notificationPermissionRequestPending;
    private final Object notificationRouteLock = new Object();
    private String pendingNotificationThreadId;
    private String pendingNotificationRouteAttempt;
    private boolean notificationRouteInFlight;
    private long pendingNotificationReceivedAt;
    private int notificationRouteAttempts;
    private PendingDownload pendingDownload;
    private PendingBlobRequest pendingBlobRequest;
    private ValueCallback<Uri[]> fileChooserCallback;
    private boolean downloadReceiverRegistered;
    private boolean notificationStatusReceiverRegistered;
    private boolean backNavigationPending;
    private final Handler captureMaintenanceHandler = new Handler(Looper.getMainLooper());
    private final Runnable captureMaintenanceTask = new Runnable() {
        @Override
        public void run() {
            cleanupStaleCaptureFiles();
            captureMaintenanceHandler.postDelayed(this, CAPTURE_SWEEP_INTERVAL_MS);
        }
    };

    private final BroadcastReceiver downloadReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (!DownloadManager.ACTION_DOWNLOAD_COMPLETE.equals(intent.getAction())) {
                return;
            }
            long id = intent.getLongExtra(DownloadManager.EXTRA_DOWNLOAD_ID, -1L);
            DownloadRecord record = downloads.remove(id);
            if (record != null) {
                handleCompletedDownload(id, record);
            }
        }
    };

    private final BroadcastReceiver notificationStatusReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (CodexNotificationService.ACTION_STATUS.equals(intent.getAction())) {
                dispatchNotificationStatus();
            }
        }
    };

    @Override
    public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(Color.rgb(12, 15, 14));
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    OnBackInvokedDispatcher.PRIORITY_DEFAULT,
                    this::handleBackNavigation);
        }
        registerDownloadReceiver();
        registerNotificationStatusReceiver();
        cleanupStaleCaptureFiles();
        captureMaintenanceHandler.postDelayed(
                captureMaintenanceTask,
                CAPTURE_SWEEP_INTERVAL_MS);

        server = getPreferences(MODE_PRIVATE)
                .getString("server", "");
        boolean openedFromNotification = captureNotificationIntent(getIntent());
        if (!openedFromNotification && state != null) {
            String restoredThreadId = state.getString(STATE_PENDING_NOTIFICATION_THREAD);
            long restoredAt = state.getLong(STATE_PENDING_NOTIFICATION_AT, 0L);
            if (restoredThreadId != null
                    && restoredAt > 0 && System.currentTimeMillis() - restoredAt < NOTIFICATION_ROUTE_TTL_MS
                    && restoredThreadId.length() <= 200
                    && restoredThreadId.matches("[A-Za-z0-9._:/\\-]+")) {
                synchronized (notificationRouteLock) {
                    pendingNotificationThreadId = restoredThreadId;
                    pendingNotificationReceivedAt = restoredAt;
                    pendingNotificationRouteAttempt = null;
                    notificationRouteInFlight = false;
                }
                openedFromNotification = true;
            }
        }
        if (openedFromNotification) {
            NotificationConfigStore.Credentials credentials =
                    NotificationConfigStore.credentials(this);
            if (credentials != null) {
                server = credentials.server;
                getPreferences(MODE_PRIVATE).edit().putString("server", server).apply();
            }
        }
        Bundle webState = state == null ? null : state.getBundle(STATE_WEB_VIEW);
        // A notification is authoritative: never restore a WebView snapshot that may still
        // point at a previously selected machine. Load the encrypted monitor server afresh.
        if (openedFromNotification) {
            showWeb();
        } else if (state != null && state.getBoolean(STATE_WEB_ACTIVE, false)) {
            showWeb(webState);
        } else {
            showConnect();
        }
    }

    private TextView text(String value, int sp, int color) {
        TextView result = new TextView(this);
        result.setText(value);
        result.setTextSize(sp);
        result.setTextColor(color);
        return result;
    }

    private void showConnect() {
        destroyWebView();

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER);
        root.setPadding(42, 42, 42, 42);
        root.setBackgroundColor(Color.rgb(12, 15, 14));

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(30, 30, 30, 30);
        card.setBackgroundColor(Color.rgb(21, 26, 24));

        TextView title = text("Codex LAN Console", 26, Color.WHITE);
        title.setGravity(Gravity.CENTER);
        card.addView(title, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));

        TextView help = text(
                "输入电脑的局域网或 Tailscale 地址。无需登录其他账号。",
                14,
                Color.rgb(145, 160, 154));
        help.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams helpParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
        helpParams.setMargins(0, 16, 0, 22);
        card.addView(help, helpParams);

        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setText(server);
        input.setHint("http://100.x.y.z:8787");
        input.setTextColor(Color.WHITE);
        input.setHintTextColor(Color.GRAY);
        input.setBackgroundColor(Color.rgb(12, 15, 14));
        input.setPadding(18, 15, 18, 15);
        card.addView(input, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));

        Button connect = new Button(this);
        connect.setText("连接电脑");
        connect.setTextColor(Color.rgb(8, 32, 22));
        connect.setBackgroundColor(Color.rgb(123, 241, 189));
        LinearLayout.LayoutParams buttonParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
        buttonParams.setMargins(0, 18, 0, 0);
        card.addView(connect, buttonParams);

        connect.setOnClickListener(view -> {
            String value = input.getText().toString().trim();
            if (!value.startsWith("http://") && !value.startsWith("https://")) {
                value = "http://" + value;
            }
            server = value.replaceAll("/+$", "");
            getPreferences(MODE_PRIVATE).edit().putString("server", server).apply();
            showWeb();
        });

        root.addView(card, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));
        setContentView(root);
    }

    private void showWeb() {
        showWeb(null);
    }

    private void showWeb(Bundle restoredState) {
        destroyWebView();

        web = new WebView(this);
        web.setBackgroundColor(Color.rgb(12, 15, 14));

        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setCacheMode(WebSettings.LOAD_DEFAULT);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(true);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setSupportMultipleWindows(false);
        settings.setUserAgentString(
                settings.getUserAgentString() + " CodexLanConsole/1.7.7");

        CookieManager cookies = CookieManager.getInstance();
        cookies.setAcceptCookie(true);
        cookies.setAcceptThirdPartyCookies(web, false);

        web.addJavascriptInterface(blobBridge, "CodexAndroidDownloads");
        web.addJavascriptInterface(notificationBridge, "CodexAndroidNotifications");
        web.addJavascriptInterface(new ScooterBridge(), "CodexAndroidScooter");
        web.setWebViewClient(new ConsoleWebViewClient());
        web.setWebChromeClient(new ConsoleWebChromeClient());
        web.setDownloadListener(this::requestDownload);

        setContentView(web);
        if (restoredState == null || web.restoreState(restoredState) == null) {
            web.loadUrl(server);
        }
    }

    @Override
    protected void onSaveInstanceState(Bundle outState) {
        if (web != null) {
            Bundle webState = new Bundle();
            web.saveState(webState);
            outState.putBoolean(STATE_WEB_ACTIVE, true);
            outState.putBundle(STATE_WEB_VIEW, webState);
        }
        synchronized (notificationRouteLock) {
            if (pendingNotificationThreadId != null) {
                outState.putString(
                        STATE_PENDING_NOTIFICATION_THREAD,
                        pendingNotificationThreadId);
                outState.putLong(STATE_PENDING_NOTIFICATION_AT, pendingNotificationReceivedAt);
            }
        }
        super.onSaveInstanceState(outState);
    }

    private final class ConsoleWebViewClient extends WebViewClient {
        @Override
        public void onPageStarted(WebView view, String url, android.graphics.Bitmap favicon) {
            // Do not expose privileged native actions based only on a requested URL. Redirects
            // and provisional loads are untrusted until WebView commits the exact console origin.
            trustedConsoleOrigin = false;
            consoleMainFrameLoadFailed = false;
            resetNotificationRouteAttempt();
            super.onPageStarted(view, url, favicon);
        }

        @Override
        public void onPageCommitVisible(WebView view, String url) {
            handleCommittedConsolePage(url);
            super.onPageCommitVisible(view, url);
        }

        @Override
        public void onPageFinished(WebView view, String url) {
            handleCommittedConsolePage(url);
            super.onPageFinished(view, url);
        }

        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            if (request.isForMainFrame() && request.hasGesture()) clearPendingNotificationRoute();
            return handleNavigation(view, request.getUrl());
        }

        @Override
        public void onReceivedError(
                WebView view,
                WebResourceRequest request,
                WebResourceError error) {
            if (request.isForMainFrame()) {
                trustedConsoleOrigin = false;
                consoleMainFrameLoadFailed = true;
                resetNotificationRouteAttempt();
                showError(error.getDescription().toString());
            }
        }

        @Override
        public void onReceivedHttpError(
                WebView view,
                WebResourceRequest request,
                android.webkit.WebResourceResponse errorResponse) {
            if (request.isForMainFrame() && errorResponse.getStatusCode() >= 400) {
                trustedConsoleOrigin = false;
                consoleMainFrameLoadFailed = true;
                resetNotificationRouteAttempt();
            }
            super.onReceivedHttpError(view, request, errorResponse);
        }
    }

    private final class ConsoleWebChromeClient extends WebChromeClient {
        @Override
        public boolean onShowFileChooser(
                WebView view,
                ValueCallback<Uri[]> callback,
                FileChooserParams params) {
            return launchFileChooser(callback, params);
        }
    }

    private boolean handleNavigation(WebView view, Uri uri) {
        String scheme = lower(uri.getScheme());
        if ("http".equals(scheme) || "https".equals(scheme)) {
            if (isLoopbackHost(uri.getHost())) {
                Uri rewritten = rewriteLoopbackUrl(uri);
                if (rewritten != null) {
                    Toast.makeText(this, "已改用远程电脑地址", Toast.LENGTH_SHORT).show();
                    view.loadUrl(rewritten.toString());
                    return true;
                }
            }
            if (isRemoteConsoleHost(uri.getHost())) {
                return false;
            }
            return openExternal(uri);
        }

        if ("mailto".equals(scheme)
                || "tel".equals(scheme)
                || "sms".equals(scheme)
                || "geo".equals(scheme)) {
            return openExternal(uri);
        }
        return false;
    }

    private boolean isConfiguredConsoleOrigin(String url) {
        if (url == null || server == null) {
            return false;
        }
        Uri page = Uri.parse(url);
        Uri configured = Uri.parse(server);
        return lower(page.getScheme()).equals(lower(configured.getScheme()))
                && lower(page.getHost()).equals(lower(configured.getHost()))
                && effectivePort(page) == effectivePort(configured);
    }

    private void handleCommittedConsolePage(String url) {
        trustedConsoleOrigin = !consoleMainFrameLoadFailed
                && isConfiguredConsoleOrigin(url);
        if (!trustedConsoleOrigin) {
            resetNotificationRouteAttempt();
            return;
        }
        dispatchNotificationStatus();
        openPendingNotificationThread();
    }

    private static int effectivePort(Uri uri) {
        if (uri.getPort() >= 0) {
            return uri.getPort();
        }
        return "https".equalsIgnoreCase(uri.getScheme()) ? 443 : 80;
    }

    private boolean captureNotificationIntent(Intent intent) {
        if (intent == null
                || !intent.getBooleanExtra(CodexNotificationService.EXTRA_OPEN_CONSOLE, false)) {
            return false;
        }
        intent.removeExtra(CodexNotificationService.EXTRA_OPEN_CONSOLE);
        String threadId = intent.getStringExtra(CodexNotificationService.EXTRA_THREAD_ID);
        intent.removeExtra(CodexNotificationService.EXTRA_THREAD_ID);
        if (threadId == null || threadId.length() > 200
                || !threadId.matches("[A-Za-z0-9._:/\\-]+")) {
            synchronized (notificationRouteLock) {
                pendingNotificationThreadId = null;
                pendingNotificationRouteAttempt = null;
                notificationRouteInFlight = false;
            }
            return true;
        }
        synchronized (notificationRouteLock) {
            pendingNotificationThreadId = threadId;
            pendingNotificationRouteAttempt = null;
            notificationRouteInFlight = false;
            pendingNotificationReceivedAt = System.currentTimeMillis();
            notificationRouteAttempts = 0;
        }
        return true;
    }

    private void openPendingNotificationThread() {
        WebView currentWeb = web;
        if (currentWeb == null || !trustedConsoleOrigin) {
            return;
        }
        String threadId;
        String attempt;
        long receivedAt;
        synchronized (notificationRouteLock) {
            if (pendingNotificationThreadId != null
                    && (notificationRouteAttempts >= 6
                    || System.currentTimeMillis() - pendingNotificationReceivedAt > NOTIFICATION_ROUTE_TTL_MS)) {
                clearPendingNotificationRoute();
            }
            if (pendingNotificationThreadId == null || notificationRouteInFlight) {
                return;
            }
            threadId = pendingNotificationThreadId;
            attempt = UUID.randomUUID().toString();
            pendingNotificationRouteAttempt = attempt;
            notificationRouteInFlight = true;
            receivedAt = pendingNotificationReceivedAt;
            notificationRouteAttempts++;
        }
        String encoded = JSONObject.quote(threadId);
        String encodedAttempt = JSONObject.quote(attempt);
        currentWeb.evaluateJavascript(
                "(() => { const id=" + encoded + ",attempt=" + encodedAttempt + ";"
                        + "const ack=ok=>{try{window.CodexAndroidNotifications"
                        + ".acknowledgeThreadOpen(id,attempt,Boolean(ok));}catch(_){}};"
                        + "try{if(typeof window.CodexConsoleReceiveNotification==='function')"
                        + "{ack(window.CodexConsoleReceiveNotification(id,attempt," + receivedAt + "));}"
                        + "else if(typeof window.CodexConsoleOpenThread==='function')"
                        + "{ack(window.CodexConsoleOpenThread(id)!==false);}"
                        + "else{ack(false);}}catch(_){ack(false);}return true;})()",
                value -> {
                    if (!"true".equalsIgnoreCase(value)) {
                        acknowledgeNotificationRoute(threadId, attempt, false);
                    }
                });
    }

    private void acknowledgeNotificationRoute(
            String threadId,
            String attempt,
            boolean succeeded) {
        synchronized (notificationRouteLock) {
            if (!attempt.equals(pendingNotificationRouteAttempt)
                    || !threadId.equals(pendingNotificationThreadId)) {
                return;
            }
            notificationRouteInFlight = false;
            pendingNotificationRouteAttempt = null;
            if (succeeded) {
                clearPendingNotificationRoute();
            } else if (notificationRouteAttempts < 6) {
                // Only retry frontend readiness briefly, not task-data loading.
                runOnUiThread(() -> captureMaintenanceHandler.postDelayed(
                        MainActivity.this::openPendingNotificationThread, 500L));
            }
        }
    }

    private void clearPendingNotificationRoute() {
        synchronized (notificationRouteLock) {
            pendingNotificationThreadId = null;
            pendingNotificationRouteAttempt = null;
            notificationRouteInFlight = false;
            pendingNotificationReceivedAt = 0L;
            notificationRouteAttempts = 0;
        }
    }

    private void resetNotificationRouteAttempt() {
        synchronized (notificationRouteLock) {
            pendingNotificationRouteAttempt = null;
            notificationRouteInFlight = false;
        }
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        if (!captureNotificationIntent(intent)) {
            return;
        }
        NotificationConfigStore.Credentials credentials =
                NotificationConfigStore.credentials(this);
        if (credentials != null && !credentials.server.equals(server)) {
            server = credentials.server;
            getPreferences(MODE_PRIVATE).edit().putString("server", server).apply();
            showWeb();
        } else if (web == null || !trustedConsoleOrigin) {
            showWeb();
        } else {
            openPendingNotificationThread();
        }
    }

    private boolean openExternal(Uri uri) {
        try {
            Intent intent = new Intent(Intent.ACTION_VIEW, uri);
            if (intent.resolveActivity(getPackageManager()) == null) {
                return false;
            }
            startActivity(intent);
            return true;
        } catch (RuntimeException error) {
            return false;
        }
    }

    private boolean isRemoteConsoleHost(String host) {
        if (host == null) {
            return false;
        }
        String serverHost = Uri.parse(server).getHost();
        if (serverHost != null && serverHost.equalsIgnoreCase(host)) {
            return true;
        }
        String normalized = lower(host);
        if (normalized.endsWith(".ts.net")) {
            return true;
        }
        String[] parts = normalized.split("\\.");
        if (parts.length != 4) {
            return false;
        }
        try {
            int first = Integer.parseInt(parts[0]);
            int second = Integer.parseInt(parts[1]);
            return first == 10
                    || (first == 172 && second >= 16 && second <= 31)
                    || (first == 192 && second == 168)
                    || (first == 100 && second >= 64 && second <= 127);
        } catch (NumberFormatException ignored) {
            return false;
        }
    }

    private boolean isLoopbackHost(String host) {
        if (host == null) {
            return false;
        }
        String normalized = lower(host);
        return "localhost".equals(normalized)
                || normalized.endsWith(".localhost")
                || "0.0.0.0".equals(normalized)
                || "::".equals(normalized)
                || "::1".equals(normalized)
                || normalized.startsWith("127.");
    }

    private Uri rewriteLoopbackUrl(Uri uri) {
        Uri serverUri = Uri.parse(server);
        String host = serverUri.getHost();
        if (host == null) {
            return null;
        }
        String displayHost = host.contains(":") ? "[" + host + "]" : host;
        String authority = uri.getPort() >= 0
                ? displayHost + ":" + uri.getPort()
                : displayHost;
        return uri.buildUpon().encodedAuthority(authority).build();
    }

    private boolean launchFileChooser(
            ValueCallback<Uri[]> callback,
            WebChromeClient.FileChooserParams params) {
        if (fileChooserCallback != null) {
            fileChooserCallback.onReceiveValue(null);
        }
        fileChooserCallback = callback;
        clearCaptureTargets(true);

        String[] mimeTypes = normalizeAcceptTypes(params.getAcceptTypes());
        Intent files = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        files.addCategory(Intent.CATEGORY_OPENABLE);
        files.setType(primaryMimeType(mimeTypes));
        files.putExtra(Intent.EXTRA_MIME_TYPES, mimeTypes);
        files.putExtra(
                Intent.EXTRA_ALLOW_MULTIPLE,
                params.getMode() == WebChromeClient.FileChooserParams.MODE_OPEN_MULTIPLE);
        files.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
                | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);

        ArrayList<Intent> captureIntents = new ArrayList<>();
        boolean wildcardCapture = params.isCaptureEnabled()
                && Arrays.asList(mimeTypes).contains("*/*");
        if (acceptsType(mimeTypes, "image/") || wildcardCapture) {
            Intent camera = createCaptureIntent(MediaStore.ACTION_IMAGE_CAPTURE, ".jpg", "image/jpeg");
            if (camera != null) {
                captureIntents.add(camera);
            }
        }
        if (acceptsType(mimeTypes, "video/")) {
            Intent video = createCaptureIntent(MediaStore.ACTION_VIDEO_CAPTURE, ".mp4", "video/mp4");
            if (video != null) {
                captureIntents.add(video);
            }
        }

        Intent chooser = Intent.createChooser(files, "选择要发送的文件");
        if (!captureIntents.isEmpty()) {
            chooser.putExtra(Intent.EXTRA_INITIAL_INTENTS, captureIntents.toArray(new Intent[0]));
        }
        try {
            startActivityForResult(chooser, FILE_CHOOSER_REQUEST);
            return true;
        } catch (RuntimeException error) {
            fileChooserCallback = null;
            clearCaptureTargets(true);
            callback.onReceiveValue(null);
            Toast.makeText(this, "无法打开系统文件选择器", Toast.LENGTH_LONG).show();
            return true;
        }
    }

    private Intent createCaptureIntent(String action, String suffix, String mimeType) {
        try {
            Intent intent = new Intent(action);
            if (intent.resolveActivity(getPackageManager()) == null) {
                return null;
            }

            File file = File.createTempFile(
                    "codex-" + System.currentTimeMillis() + "-",
                    suffix,
                    LocalFileProvider.captureRoot(this));
            Uri uri = LocalFileProvider.uriForCapture(this, file);
            int grants = Intent.FLAG_GRANT_READ_URI_PERMISSION
                    | Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
            intent.putExtra(MediaStore.EXTRA_OUTPUT, uri);
            intent.setClipData(ClipData.newRawUri("capture", uri));
            intent.addFlags(grants);

            for (ResolveInfo target : getPackageManager()
                    .queryIntentActivities(intent, PackageManager.MATCH_DEFAULT_ONLY)) {
                grantUriPermission(target.activityInfo.packageName, uri, grants);
            }
            captureTargets.add(new CaptureTarget(uri, file, mimeType));
            return intent;
        } catch (IOException | RuntimeException error) {
            return null;
        }
    }

    private String[] normalizeAcceptTypes(String[] rawTypes) {
        LinkedHashSet<String> types = new LinkedHashSet<>();
        if (rawTypes != null) {
            for (String raw : rawTypes) {
                if (raw == null) {
                    continue;
                }
                for (String item : raw.split(",")) {
                    String type = item.trim().toLowerCase(Locale.ROOT);
                    if (type.startsWith(".")) {
                        String mapped = MimeTypeMap.getSingleton()
                                .getMimeTypeFromExtension(type.substring(1));
                        if (mapped != null) {
                            types.add(mapped);
                        }
                    } else if (type.contains("/")) {
                        types.add(type);
                    }
                }
            }
        }
        if (types.isEmpty()) {
            types.add("*/*");
        }
        return types.toArray(new String[0]);
    }

    private String primaryMimeType(String[] types) {
        if (types.length == 1) {
            return types[0];
        }
        boolean images = Arrays.stream(types).allMatch(type -> type.startsWith("image/"));
        boolean videos = Arrays.stream(types).allMatch(type -> type.startsWith("video/"));
        return images ? "image/*" : videos ? "video/*" : "*/*";
    }

    private boolean acceptsType(String[] types, String prefix) {
        for (String type : types) {
            if (type.startsWith(prefix)) {
                return true;
            }
        }
        return false;
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != FILE_CHOOSER_REQUEST) {
            return;
        }

        ValueCallback<Uri[]> callback = fileChooserCallback;
        fileChooserCallback = null;
        if (callback == null) {
            clearCaptureTargets(true);
            return;
        }

        ArrayList<Uri> results = new ArrayList<>();
        if (resultCode == RESULT_OK) {
            collectSelectedUris(data, results);
            if (results.isEmpty()) {
                for (CaptureTarget target : captureTargets) {
                    if (target.file.length() > 0) {
                        results.add(target.uri);
                        break;
                    }
                }
            }
        }

        Uri[] returned = results.isEmpty() ? null : results.toArray(new Uri[0]);
        try {
            callback.onReceiveValue(returned);
        } finally {
            releaseCaptureTargets(results);
        }
    }

    private void collectSelectedUris(Intent data, ArrayList<Uri> results) {
        if (data == null) {
            return;
        }
        LinkedHashSet<String> seen = new LinkedHashSet<>();
        ClipData clip = data.getClipData();
        if (clip != null) {
            for (int index = 0; index < clip.getItemCount(); index++) {
                addSelectedUri(clip.getItemAt(index).getUri(), data, seen, results);
            }
        } else {
            addSelectedUri(data.getData(), data, seen, results);
        }
    }

    private void addSelectedUri(
            Uri uri,
            Intent data,
            LinkedHashSet<String> seen,
            ArrayList<Uri> results) {
        if (uri == null || !seen.add(uri.toString())) {
            return;
        }
        int flags = data.getFlags()
                & (Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        if ((flags & Intent.FLAG_GRANT_READ_URI_PERMISSION) != 0) {
            try {
                getContentResolver().takePersistableUriPermission(
                        uri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION);
            } catch (SecurityException ignored) {
                // A camera/content provider may grant only a transient URI, which is sufficient here.
            }
        }
        results.add(uri);
    }

    private void clearCaptureTargets(boolean deleteAll) {
        int grants = Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
        for (CaptureTarget target : captureTargets) {
            try {
                revokeUriPermission(target.uri, grants);
            } catch (RuntimeException ignored) {
                // Some camera apps revoke their own grant after returning.
            }
            if (deleteAll || target.file.length() == 0) {
                //noinspection ResultOfMethodCallIgnored
                target.file.delete();
            }
        }
        captureTargets.clear();
    }

    private void releaseCaptureTargets(List<Uri> returnedUris) {
        LinkedHashSet<String> returned = new LinkedHashSet<>();
        for (Uri uri : returnedUris) returned.add(uri.toString());
        int grants = Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
        for (CaptureTarget target : captureTargets) {
            try {
                revokeUriPermission(target.uri, grants);
            } catch (RuntimeException ignored) {
                // Some camera apps revoke their own grant after returning.
            }
            if (target.file.length() > 0 && returned.contains(target.uri.toString())) {
                scheduleCaptureCleanup(target.file);
            } else {
                //noinspection ResultOfMethodCallIgnored
                target.file.delete();
            }
        }
        captureTargets.clear();
    }

    private void scheduleCaptureCleanup(File file) {
        File root = LocalFileProvider.captureRoot(getApplicationContext());
        DELAYED_FILE_CLEANUP.postDelayed(
                () -> deleteCaptureWithinRoot(root, file),
                CAPTURE_DELETE_DELAY_MS);
    }

    private void cleanupStaleCaptureFiles() {
        File root;
        try {
            root = LocalFileProvider.captureRoot(getApplicationContext()).getCanonicalFile();
        } catch (IOException | RuntimeException error) {
            return;
        }
        File[] files = root.listFiles();
        if (files == null) return;
        long cutoff = System.currentTimeMillis() - CAPTURE_STALE_AFTER_MS;
        for (File file : files) {
            if (file.isFile() && file.lastModified() > 0 && file.lastModified() < cutoff) {
                deleteCaptureWithinRoot(root, file);
            }
        }
    }

    private static void deleteCaptureWithinRoot(File root, File file) {
        try {
            File canonicalRoot = root.getCanonicalFile();
            File canonicalFile = file.getCanonicalFile();
            if (canonicalRoot.equals(canonicalFile.getParentFile())) {
                //noinspection ResultOfMethodCallIgnored
                canonicalFile.delete();
            }
        } catch (IOException | RuntimeException ignored) {
            // Best-effort cleanup only; a later sweep will try again.
        }
    }

    private void requestDownload(
            String url,
            String userAgent,
            String contentDisposition,
            String mimeType,
            long contentLength) {
        Uri uri;
        try {
            uri = Uri.parse(url);
        } catch (RuntimeException error) {
            showDownloadError("下载地址无效");
            return;
        }

        String scheme = lower(uri.getScheme());
        if ("blob".equals(scheme)) {
            PendingBlobRequest request = new PendingBlobRequest(
                    url,
                    suggestedFilename(url, contentDisposition, mimeType),
                    mimeType);
            if (needsLegacyStoragePermission()) {
                pendingBlobRequest = request;
                requestStoragePermission();
            } else {
                startBlobDownload(request);
            }
            return;
        }

        if (!"http".equals(scheme) && !"https".equals(scheme)) {
            showDownloadError("当前下载格式不受支持");
            return;
        }

        PendingDownload download = new PendingDownload(
                url,
                userAgent,
                contentDisposition,
                mimeType,
                contentLength);
        if (needsLegacyStoragePermission()) {
            pendingDownload = download;
            requestStoragePermission();
        } else {
            enqueueDownload(download);
        }
    }

    private boolean needsLegacyStoragePermission() {
        return Build.VERSION.SDK_INT <= Build.VERSION_CODES.P
                && checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE)
                != PackageManager.PERMISSION_GRANTED;
    }

    private void requestStoragePermission() {
        requestPermissions(
                new String[]{Manifest.permission.WRITE_EXTERNAL_STORAGE},
                STORAGE_PERMISSION_REQUEST);
    }

    private void enqueueDownload(PendingDownload download) {
        try {
            String filename = suggestedFilename(
                    download.url,
                    download.contentDisposition,
                    download.mimeType);
            String resolvedMime = resolvedMimeType(download.mimeType, filename);
            DownloadManager.Request request = new DownloadManager.Request(Uri.parse(download.url));
            request.setMimeType(resolvedMime);

            String userAgent = download.userAgent;
            if (isEmpty(userAgent) && web != null) {
                userAgent = web.getSettings().getUserAgentString();
            }
            if (!isEmpty(userAgent)) {
                request.addRequestHeader("User-Agent", userAgent);
            }

            CookieManager cookies = CookieManager.getInstance();
            cookies.flush();
            String cookie = cookies.getCookie(download.url);
            if (!isEmpty(cookie)) {
                request.addRequestHeader("Cookie", cookie);
            }
            if (web != null && web.getUrl() != null && web.getUrl().startsWith("http")) {
                request.addRequestHeader("Referer", web.getUrl());
            }

            request.setTitle(filename);
            request.setDescription("正在从 Codex Console 下载");
            request.setAllowedOverMetered(true);
            request.setAllowedOverRoaming(true);
            request.setNotificationVisibility(
                    DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED);
            request.setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, filename);

            DownloadManager manager = downloadManager();
            if (manager == null) {
                showDownloadError("系统下载服务不可用");
                return;
            }

            long id = manager.enqueue(request);
            downloads.put(id, new DownloadRecord(filename, resolvedMime));
            Toast.makeText(
                    this,
                    "已开始下载，文件将保存到 Downloads",
                    Toast.LENGTH_LONG).show();
        } catch (RuntimeException error) {
            showDownloadError("无法开始下载：" + safeMessage(error));
        }
    }

    private void startBlobDownload(PendingBlobRequest request) {
        if (web == null) {
            showDownloadError("页面已经关闭，无法继续下载");
            return;
        }
        String token = UUID.randomUUID().toString();
        if (!blobBridge.authorize(token, request)) {
            showDownloadError("已有一个网页文件正在下载，请稍后再试");
            return;
        }

        String script = "(async()=>{"
                + "const A=window.CodexAndroidDownloads,t=" + JSONObject.quote(token)
                + ",u=" + JSONObject.quote(request.url)
                + ",m=" + JSONObject.quote(request.mimeType == null ? "" : request.mimeType)
                + ",L=" + MAX_BLOB_BYTES + ";let q=null;"
                + "try{const r=await fetch(u,{credentials:'include'});"
                + "if(!r.ok)throw new Error('HTTP '+r.status);"
                + "const h=r.headers.get('content-length'),n=h===null?-1:Number(h);"
                + "const e=Number.isFinite(n)&&n>=0?n:-1;"
                + "if(e>L)throw new Error('文件超过 512 MiB 限制');"
                + "if(!r.body||typeof r.body.getReader!=='function')throw new Error('当前 WebView 不支持流式下载');"
                + "q=r.body.getReader();const c=r.headers.get('content-type')||m;"
                + "if(!A.begin(t,c,e))throw new Error('无法创建下载文件');"
                + "let w=0;for(;;){const d=await q.read();if(d.done)break;const x=d.value;"
                + "w+=x.byteLength;if(w>L){try{await q.cancel();}catch(_){}throw new Error('文件超过 512 MiB 限制');}"
                + "let s='';for(let i=0;i<x.length;i+=32768)"
                + "s+=String.fromCharCode.apply(null,x.subarray(i,Math.min(i+32768,x.length)));"
                + "if(!A.append(t,btoa(s)))throw new Error('写入下载文件失败');}"
                + "if(e>=0&&w!==e)throw new Error('下载内容长度不一致');"
                + "if(!A.finish(t))throw new Error('下载文件校验失败');"
                + "}catch(e){if(q)try{await q.cancel();}catch(_){}A.fail(t,String(e&&e.message||e));}})();";
        web.evaluateJavascript(script, null);
        Toast.makeText(this, "正在保存网页文件", Toast.LENGTH_SHORT).show();
    }

    private String suggestedFilename(String url, String contentDisposition, String mimeType) {
        return safeFilename(URLUtil.guessFileName(url, contentDisposition, mimeType));
    }

    private String safeFilename(String filename) {
        String safe = filename == null ? "download" : filename.trim();
        safe = safe.replaceAll("[\\\\/:*?\"<>|\\p{Cntrl}]", "_");
        if (safe.isEmpty() || ".".equals(safe) || "..".equals(safe)) {
            return "download";
        }
        if (safe.length() <= 180) {
            return safe;
        }
        int dot = safe.lastIndexOf('.');
        String extension = dot > 0 && safe.length() - dot <= 20 ? safe.substring(dot) : "";
        return safe.substring(0, 180 - extension.length()) + extension;
    }

    private String resolvedMimeType(String supplied, String filename) {
        String normalized = supplied == null ? "" : supplied.trim().toLowerCase(Locale.ROOT);
        if (!normalized.isEmpty() && !"application/octet-stream".equals(normalized)) {
            return normalized;
        }
        String extension = MimeTypeMap.getFileExtensionFromUrl(Uri.encode(filename));
        if ("apk".equalsIgnoreCase(extension)) {
            return APK_MIME;
        }
        String mapped = MimeTypeMap.getSingleton().getMimeTypeFromExtension(
                extension == null ? "" : extension.toLowerCase(Locale.ROOT));
        return mapped == null ? (normalized.isEmpty() ? "application/octet-stream" : normalized) : mapped;
    }

    @SuppressLint("UnspecifiedRegisterReceiverFlag")
    private void registerDownloadReceiver() {
        IntentFilter filter = new IntentFilter(DownloadManager.ACTION_DOWNLOAD_COMPLETE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(downloadReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(downloadReceiver, filter);
        }
        downloadReceiverRegistered = true;
    }

    @SuppressLint("UnspecifiedRegisterReceiverFlag")
    private void registerNotificationStatusReceiver() {
        IntentFilter filter = new IntentFilter(CodexNotificationService.ACTION_STATUS);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(
                    notificationStatusReceiver,
                    filter,
                    Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(notificationStatusReceiver, filter);
        }
        notificationStatusReceiverRegistered = true;
    }

    private DownloadManager downloadManager() {
        return (DownloadManager) getSystemService(Context.DOWNLOAD_SERVICE);
    }

    private void handleCompletedDownload(long id, DownloadRecord record) {
        DownloadManager manager = downloadManager();
        if (manager == null) {
            return;
        }
        DownloadManager.Query query = new DownloadManager.Query().setFilterById(id);
        try (Cursor cursor = manager.query(query)) {
            if (cursor == null || !cursor.moveToFirst()) {
                return;
            }
            int status = cursor.getInt(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS));
            if (status != DownloadManager.STATUS_SUCCESSFUL) {
                int reason = cursor.getInt(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_REASON));
                showDownloadError("下载失败，系统错误代码：" + reason);
                return;
            }
            Uri uri = manager.getUriForDownloadedFile(id);
            String mime = record.mimeType;
            int mimeColumn = cursor.getColumnIndex(DownloadManager.COLUMN_MEDIA_TYPE);
            if (isEmpty(mime) && mimeColumn >= 0) {
                mime = cursor.getString(mimeColumn);
            }
            if (uri != null) {
                showDownloadReady(uri, mime, record.filename);
            }
        } catch (RuntimeException error) {
            showDownloadError("下载完成，但无法读取文件信息");
        }
    }

    private void showDownloadReady(Uri uri, String mimeType, String filename) {
        runOnUiThread(() -> new AlertDialog.Builder(this)
                .setTitle("下载完成")
                .setMessage(filename + "\n\n文件已保存到 Downloads。")
                .setPositiveButton("打开", (dialog, which) -> openDownloadedFile(uri, mimeType))
                .setNegativeButton("稍后", null)
                .show());
    }

    private void openDownloadedFile(Uri uri, String mimeType) {
        String mime = isEmpty(mimeType) ? "*/*" : mimeType;
        if (APK_MIME.equalsIgnoreCase(mime)
                && !getPackageManager().canRequestPackageInstalls()) {
            new AlertDialog.Builder(this)
                    .setTitle("允许安装此 APK")
                    .setMessage("Android 需要先允许 Codex LAN 安装未知来源应用。授权后请再次打开下载的 APK。")
                    .setPositiveButton("前往设置", (dialog, which) -> {
                        Intent settings = new Intent(
                                Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                                Uri.parse("package:" + getPackageName()));
                        startActivity(settings);
                    })
                    .setNegativeButton("取消", null)
                    .show();
            return;
        }

        Intent view = new Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, mime)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try {
            if (view.resolveActivity(getPackageManager()) == null) {
                showDownloadError("手机上没有可打开此文件的应用");
                return;
            }
            if (APK_MIME.equalsIgnoreCase(mime)) {
                startActivity(view);
            } else {
                startActivity(Intent.createChooser(view, "打开下载的文件"));
            }
        } catch (RuntimeException error) {
            showDownloadError("无法打开此文件");
        }
    }

    private void showDownloadError(String message) {
        runOnUiThread(() -> Toast.makeText(this, message, Toast.LENGTH_LONG).show());
    }

    @Override
    public void onRequestPermissionsResult(
            int requestCode,
            String[] permissions,
            int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == SCOOTER_PERMISSION_REQUEST) {
            String id = pendingScooterRideId; pendingScooterRideId = null;
            if (id != null && checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED) {
                try { ScooterTrackingService.start(this, id); } catch (RuntimeException ex) { ScooterTrackingService.problem("请回到 App 后重试启动定位。"); }
            } else ScooterTrackingService.problem("未获得精确定位权限；只记录时长，停止时请补记里程。");
            return;
        }
        if (requestCode == NOTIFICATION_PERMISSION_REQUEST) {
            notificationPermissionRequestPending = false;
            boolean granted = grantResults.length > 0
                    && grantResults[0] == PackageManager.PERMISSION_GRANTED;
            if (granted && NotificationConfigStore.isEnabled(this)) {
                CodexNotificationService.start(this);
            } else if (!granted) {
                NotificationConfigStore.setEnabled(this, false);
            }
            dispatchNotificationStatus();
            return;
        }
        if (requestCode != STORAGE_PERMISSION_REQUEST) {
            return;
        }

        PendingDownload http = pendingDownload;
        PendingBlobRequest blob = pendingBlobRequest;
        pendingDownload = null;
        pendingBlobRequest = null;
        if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
            if (http != null) {
                enqueueDownload(http);
            }
            if (blob != null) {
                startBlobDownload(blob);
            }
        } else {
            showDownloadError("未获得存储权限，无法保存下载文件");
        }
    }

    private void showError(String message) {
        runOnUiThread(() -> new AlertDialog.Builder(this)
                .setTitle("无法连接电脑")
                .setMessage(message
                        + "\n\n请确认电脑端程序已启动，并且手机可以通过局域网或 Tailscale 访问它。")
                .setPositiveButton("重试", (dialog, which) -> showWeb())
                .setNegativeButton("修改地址", (dialog, which) -> showConnect())
                .show());
    }

    private void destroyWebView() {
        backNavigationPending = false;
        trustedConsoleOrigin = false;
        consoleMainFrameLoadFailed = false;
        resetNotificationRouteAttempt();
        if (fileChooserCallback != null) {
            fileChooserCallback.onReceiveValue(null);
            fileChooserCallback = null;
        }
        clearCaptureTargets(true);
        blobBridge.cancel();
        if (web == null) {
            return;
        }
        web.stopLoading();
        web.removeJavascriptInterface("CodexAndroidDownloads");
        web.removeJavascriptInterface("CodexAndroidNotifications");
        web.removeJavascriptInterface("CodexAndroidScooter");
        web.setDownloadListener(null);
        web.setWebChromeClient(null);
        web.setWebViewClient(null);
        web.destroy();
        web = null;
    }

    @Override
    protected void onPause() {
        CookieManager.getInstance().flush();
        super.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (NotificationConfigStore.isEnabled(this)
                && NotificationConfigStore.isConfigured(this)
                && CodexNotificationService.hasNotificationPermission(this)) {
            CodexNotificationService.start(this);
        }
        dispatchNotificationStatus();
        openPendingNotificationThread();
    }

    @Override
    protected void onDestroy() {
        captureMaintenanceHandler.removeCallbacks(captureMaintenanceTask);
        CookieManager.getInstance().flush();
        destroyWebView();
        if (downloadReceiverRegistered) {
            unregisterReceiver(downloadReceiver);
            downloadReceiverRegistered = false;
        }
        if (notificationStatusReceiverRegistered) {
            unregisterReceiver(notificationStatusReceiver);
            notificationStatusReceiverRegistered = false;
        }
        super.onDestroy();
    }

    @SuppressLint("GestureBackNavigation")
    @Override
    public void onBackPressed() {
        handleBackNavigation();
    }

    private void handleBackNavigation() {
        clearPendingNotificationRoute();
        WebView currentWeb = web;
        if (currentWeb == null) {
            finish();
            return;
        }

        // The console is a single-page app. Let it close transient layers and move
        // through its own history before consulting the surrounding WebView history.
        if (backNavigationPending) {
            return;
        }
        backNavigationPending = true;
        currentWeb.evaluateJavascript(
                "(() => { try { return typeof window.CodexConsoleHandleBack === 'function'"
                        + " && window.CodexConsoleHandleBack() === true; } catch (_) { return false; } })()",
                value -> {
                    if (web != currentWeb) {
                        return;
                    }
                    if ("true".equalsIgnoreCase(value)) {
                        currentWeb.postDelayed(() -> {
                            if (web == currentWeb) {
                                backNavigationPending = false;
                            }
                        }, 350L);
                        return;
                    }
                    if (currentWeb.canGoBack()) {
                        currentWeb.goBack();
                        currentWeb.postDelayed(() -> {
                            if (web == currentWeb) {
                                backNavigationPending = false;
                            }
                        }, 350L);
                    } else {
                        backNavigationPending = false;
                        finish();
                    }
                });
    }

    private static String lower(String value) {
        return value == null ? "" : value.toLowerCase(Locale.ROOT);
    }

    private static boolean isEmpty(String value) {
        return value == null || value.trim().isEmpty();
    }

    private static String safeMessage(Throwable error) {
        return error.getMessage() == null ? error.getClass().getSimpleName() : error.getMessage();
    }

    private String notificationPermissionState() {
        NotificationManager manager = getSystemService(NotificationManager.class);
        boolean globallyEnabled = manager == null || manager.areNotificationsEnabled();
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            return globallyEnabled && CodexNotificationService.alertChannelEnabled(this)
                    ? "granted"
                    : "blocked";
        }
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                == PackageManager.PERMISSION_GRANTED) {
            return globallyEnabled && CodexNotificationService.alertChannelEnabled(this)
                    ? "granted"
                    : "blocked";
        }
        if (notificationPermissionRequestPending
                || !NotificationConfigStore.wasPermissionRequested(this)) {
            return "required";
        }
        return shouldShowRequestPermissionRationale(Manifest.permission.POST_NOTIFICATIONS)
                ? "denied"
                : "blocked";
    }

    private String notificationStatusJson() {
        try {
            PowerManager power = getSystemService(PowerManager.class);
            boolean batteryExempt = power != null
                    && power.isIgnoringBatteryOptimizations(getPackageName());
            ActivityManager activity = getSystemService(ActivityManager.class);
            boolean backgroundRestricted = Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
                    && activity != null
                    && activity.isBackgroundRestricted();
            return new JSONObject()
                    .put("supported", true)
                    .put("configured", NotificationConfigStore.isConfigured(this))
                    .put("enabled", NotificationConfigStore.isEnabled(this))
                    .put("permission", notificationPermissionState())
                    .put("alertsEnabled", CodexNotificationService.alertChannelEnabled(this))
                    .put("serviceRunning", CodexNotificationService.isRunning())
                    .put("batteryExempt", batteryExempt)
                    .put("batteryOptimized", !batteryExempt)
                    .put("backgroundRestricted", backgroundRestricted)
                    .put("manufacturer", Build.MANUFACTURER == null ? "" : Build.MANUFACTURER)
                    .put("lastError", NotificationConfigStore.lastError(this))
                    .toString();
        } catch (org.json.JSONException error) {
            return "{\"supported\":false}";
        }
    }

    private void dispatchNotificationStatus() {
        runOnUiThread(() -> {
            WebView currentWeb = web;
            if (currentWeb == null || !trustedConsoleOrigin) {
                return;
            }
            currentWeb.evaluateJavascript(
                    "window.dispatchEvent(new CustomEvent('codex-notification-status',"
                            + "{detail:" + notificationStatusJson() + "}));",
                    null);
        });
    }

    private final class ScooterBridge {
        @JavascriptInterface public String status() { return ScooterTrackingService.status(); }
        @JavascriptInterface public String start(String rideId) {
            if (!trustedConsoleOrigin || rideId == null || !rideId.matches("[0-9a-f]{32}")) return "无效的骑行请求";
            if (NotificationConfigStore.credentials(MainActivity.this) == null) return "请先完成手机 App 配对";
            boolean permission = checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED;
            runOnUiThread(() -> {
                if (!permission) {
                    pendingScooterRideId = rideId;
                    requestPermissions(Build.VERSION.SDK_INT >= 33 ? new String[]{Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION, Manifest.permission.POST_NOTIFICATIONS}
                            : new String[]{Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION}, SCOOTER_PERMISSION_REQUEST);
                } else try { ScooterTrackingService.start(MainActivity.this, rideId); }
                catch (RuntimeException ex) { ScooterTrackingService.problem("系统暂未允许后台记录，请返回 App 后重试。"); }
            });
            return permission ? "started" : "permission_requested";
        }
        @JavascriptInterface public void stop() {
            if (trustedConsoleOrigin) runOnUiThread(() -> ScooterTrackingService.stop(MainActivity.this));
        }
    }

    private final class NotificationBridge {
        @JavascriptInterface
        public void cancelPendingThreadOpen() {
            if (trustedConsoleOrigin) clearPendingNotificationRoute();
        }

        @JavascriptInterface
        public void acknowledgeThreadOpen(
                String threadId,
                String attempt,
                boolean succeeded) {
            if (!trustedConsoleOrigin || threadId == null || attempt == null) {
                return;
            }
            acknowledgeNotificationRoute(threadId, attempt, succeeded);
        }

        @JavascriptInterface
        public String getStatus() {
            return notificationStatusJson();
        }

        @JavascriptInterface
        public String configure(String token) {
            if (!trustedConsoleOrigin) {
                return "untrusted_origin";
            }
            boolean changed = !NotificationConfigStore.credentialsMatch(
                    MainActivity.this,
                    server,
                    token);
            if (!NotificationConfigStore.configure(MainActivity.this, server, token)) {
                return "invalid_token";
            }
            NotificationConfigStore.setLastError(MainActivity.this, "");
            CodexQuotaWidgetUpdater.renderCached(MainActivity.this);
            CodexQuotaWidgetUpdater.requestRefresh(MainActivity.this, true, null);
            if (changed && CodexNotificationService.isRunning()) {
                CodexNotificationService.reload(MainActivity.this);
            }
            dispatchNotificationStatus();
            return "configured";
        }

        @JavascriptInterface
        public String requestPermission() {
            if (!trustedConsoleOrigin) {
                return "untrusted_origin";
            }
            if (!NotificationConfigStore.isConfigured(MainActivity.this)) {
                return "not_configured";
            }
            String state = notificationPermissionState();
            if ("granted".equals(state)) {
                return "granted";
            }
            if ("blocked".equals(state)) {
                return "blocked";
            }
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
                return "granted";
            }
            notificationPermissionRequestPending = true;
            NotificationConfigStore.markPermissionRequested(MainActivity.this);
            runOnUiThread(() -> requestPermissions(
                    new String[]{Manifest.permission.POST_NOTIFICATIONS},
                    NOTIFICATION_PERMISSION_REQUEST));
            dispatchNotificationStatus();
            return "requested";
        }

        @JavascriptInterface
        public String setEnabled(boolean enabled) {
            if (!trustedConsoleOrigin) {
                return "untrusted_origin";
            }
            if (!enabled) {
                CodexNotificationService.stop(MainActivity.this, true);
                dispatchNotificationStatus();
                return "stopped";
            }
            if (!NotificationConfigStore.isConfigured(MainActivity.this)) {
                return "not_configured";
            }
            NotificationConfigStore.setEnabled(MainActivity.this, true);
            String permission = notificationPermissionState();
            if (!"granted".equals(permission)) {
                dispatchNotificationStatus();
                return "blocked".equals(permission)
                        ? "permission_blocked"
                        : "permission_required";
            }
            boolean started = CodexNotificationService.start(MainActivity.this);
            dispatchNotificationStatus();
            return started ? "started" : "unavailable";
        }

        @JavascriptInterface
        public String testNotification() {
            if (!trustedConsoleOrigin) {
                return "untrusted_origin";
            }
            if (!NotificationConfigStore.isEnabled(MainActivity.this)
                    || !CodexNotificationService.isRunning()) {
                return "not_running";
            }
            CodexNotificationService.test(MainActivity.this);
            return "sent";
        }

        @JavascriptInterface
        public String openSettings() {
            if (!trustedConsoleOrigin) {
                return "untrusted_origin";
            }
            runOnUiThread(() -> {
                try {
                    Intent settings = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                            .putExtra(Settings.EXTRA_APP_PACKAGE, getPackageName());
                    startActivity(settings);
                } catch (RuntimeException error) {
                    startActivity(new Intent(
                            Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                            Uri.parse("package:" + getPackageName())));
                }
            });
            return "opened";
        }

        @JavascriptInterface
        public String openBatterySettings() {
            if (!trustedConsoleOrigin) {
                return "untrusted_origin";
            }
            runOnUiThread(() -> {
                try {
                    startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
                } catch (RuntimeException error) {
                    startActivity(new Intent(
                            Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                            Uri.parse("package:" + getPackageName())));
                }
            });
            return "opened";
        }
    }

    private final class BlobDownloadBridge {
        private final Object lock = new Object();
        private String authorizedToken;
        private PendingBlobRequest authorizedRequest;
        private long authorizationExpiresAt;
        private BlobSession session;

        boolean authorize(String token, PendingBlobRequest request) {
            synchronized (lock) {
                if (session != null) {
                    return false;
                }
                authorizedToken = token;
                authorizedRequest = request;
                authorizationExpiresAt = System.currentTimeMillis() + 60_000L;
                return true;
            }
        }

        @JavascriptInterface
        public boolean begin(String token, String blobMimeType, long size) {
            synchronized (lock) {
                if (!isAuthorized(token) || session != null || size < -1 || size > MAX_BLOB_BYTES) {
                    return false;
                }
                String mime = isEmpty(blobMimeType)
                        ? authorizedRequest.mimeType
                        : blobMimeType;
                try {
                    session = createBlobSession(authorizedRequest.filename, mime, size);
                    return true;
                } catch (IOException | RuntimeException error) {
                    clearAuthorization();
                    showDownloadError("无法创建下载文件：" + safeMessage(error));
                    return false;
                }
            }
        }

        @JavascriptInterface
        public boolean append(String token, String base64) {
            synchronized (lock) {
                if (session == null || !token.equals(authorizedToken)) {
                    return false;
                }
                try {
                    byte[] bytes = Base64.decode(base64, Base64.DEFAULT);
                    if (session.written + bytes.length > MAX_BLOB_BYTES
                            || (session.expectedSize >= 0
                            && session.written + bytes.length > session.expectedSize)) {
                        throw new IOException("Blob size exceeded the declared length");
                    }
                    session.output.write(bytes);
                    session.written += bytes.length;
                    return true;
                } catch (IOException | IllegalArgumentException error) {
                    cleanupSession(session);
                    session = null;
                    clearAuthorization();
                    showDownloadError("写入下载文件失败");
                    return false;
                }
            }
        }

        @JavascriptInterface
        public boolean finish(String token) {
            BlobSession completed;
            synchronized (lock) {
                if (session == null || !token.equals(authorizedToken)) {
                    return false;
                }
                completed = session;
                session = null;
                clearAuthorization();
            }

            try {
                completed.output.flush();
                completed.output.close();
                if (completed.expectedSize >= 0 && completed.written != completed.expectedSize) {
                    cleanupSession(completed);
                    showDownloadError("网页文件不完整，已取消保存");
                    return false;
                }
                publishBlobSession(completed);
                showDownloadReady(completed.uri, completed.mimeType, completed.filename);
                return true;
            } catch (IOException | RuntimeException error) {
                cleanupSession(completed);
                showDownloadError("无法完成网页文件下载");
                return false;
            }
        }

        @JavascriptInterface
        public void fail(String token, String message) {
            synchronized (lock) {
                if (!token.equals(authorizedToken)) {
                    return;
                }
                cleanupSession(session);
                session = null;
                clearAuthorization();
            }
            showDownloadError("网页文件下载失败：" + (message == null ? "未知错误" : message));
        }

        void cancel() {
            synchronized (lock) {
                cleanupSession(session);
                session = null;
                clearAuthorization();
            }
        }

        private boolean isAuthorized(String token) {
            return token != null
                    && token.equals(authorizedToken)
                    && authorizedRequest != null
                    && System.currentTimeMillis() <= authorizationExpiresAt;
        }

        private void clearAuthorization() {
            authorizedToken = null;
            authorizedRequest = null;
            authorizationExpiresAt = 0L;
        }
    }

    private BlobSession createBlobSession(String filename, String mimeType, long expectedSize)
            throws IOException {
        String mime = resolvedMimeType(mimeType, filename);
        ContentResolver resolver = getContentResolver();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ContentValues values = new ContentValues();
            values.put(MediaStore.Downloads.DISPLAY_NAME, filename);
            values.put(MediaStore.Downloads.MIME_TYPE, mime);
            values.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS);
            values.put(MediaStore.Downloads.IS_PENDING, 1);
            Uri uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
            if (uri == null) {
                throw new IOException("MediaStore rejected the download");
            }
            OutputStream output = resolver.openOutputStream(uri, "w");
            if (output == null) {
                resolver.delete(uri, null, null);
                throw new IOException("Cannot open MediaStore output");
            }
            return new BlobSession(
                    filename,
                    mime,
                    expectedSize,
                    uri,
                    null,
                    new BufferedOutputStream(output),
                    true);
        }

        File file = uniqueDownloadFile(filename);
        Uri uri = LocalFileProvider.uriForDownload(this, file);
        return new BlobSession(
                file.getName(),
                mime,
                expectedSize,
                uri,
                file,
                new BufferedOutputStream(new FileOutputStream(file)),
                false);
    }

    private File uniqueDownloadFile(String filename) {
        File root = LocalFileProvider.downloadRoot();
        File candidate = new File(root, filename);
        if (!candidate.exists()) {
            return candidate;
        }
        int dot = filename.lastIndexOf('.');
        String stem = dot > 0 ? filename.substring(0, dot) : filename;
        String extension = dot > 0 ? filename.substring(dot) : "";
        for (int suffix = 1; suffix < 10_000; suffix++) {
            candidate = new File(root, stem + " (" + suffix + ")" + extension);
            if (!candidate.exists()) {
                return candidate;
            }
        }
        return new File(root, UUID.randomUUID() + extension);
    }

    private void publishBlobSession(BlobSession session) {
        if (session.mediaStore) {
            ContentValues values = new ContentValues();
            values.put(MediaStore.Downloads.IS_PENDING, 0);
            getContentResolver().update(session.uri, values, null, null);
        } else if (session.file != null) {
            MediaScannerConnection.scanFile(
                    this,
                    new String[]{session.file.getAbsolutePath()},
                    new String[]{session.mimeType},
                    null);
        }
    }

    private void cleanupSession(BlobSession session) {
        if (session == null) {
            return;
        }
        try {
            session.output.close();
        } catch (IOException ignored) {
            // Best-effort cleanup after a failed or interrupted download.
        }
        try {
            if (session.mediaStore) {
                getContentResolver().delete(session.uri, null, null);
            } else if (session.file != null) {
                //noinspection ResultOfMethodCallIgnored
                session.file.delete();
            }
        } catch (RuntimeException ignored) {
            // The OS may already have removed an incomplete MediaStore entry.
        }
    }

    private static final class CaptureTarget {
        private final Uri uri;
        private final File file;
        @SuppressWarnings("unused")
        private final String mimeType;

        private CaptureTarget(Uri uri, File file, String mimeType) {
            this.uri = uri;
            this.file = file;
            this.mimeType = mimeType;
        }
    }

    private static final class PendingDownload {
        private final String url;
        private final String userAgent;
        private final String contentDisposition;
        private final String mimeType;
        @SuppressWarnings("unused")
        private final long contentLength;

        private PendingDownload(
                String url,
                String userAgent,
                String contentDisposition,
                String mimeType,
                long contentLength) {
            this.url = url;
            this.userAgent = userAgent;
            this.contentDisposition = contentDisposition;
            this.mimeType = mimeType;
            this.contentLength = contentLength;
        }
    }

    private static final class PendingBlobRequest {
        private final String url;
        private final String filename;
        private final String mimeType;

        private PendingBlobRequest(String url, String filename, String mimeType) {
            this.url = url;
            this.filename = filename;
            this.mimeType = mimeType;
        }
    }

    private static final class DownloadRecord {
        private final String filename;
        private final String mimeType;

        private DownloadRecord(String filename, String mimeType) {
            this.filename = filename;
            this.mimeType = mimeType;
        }
    }

    private static final class BlobSession {
        private final String filename;
        private final String mimeType;
        private final long expectedSize;
        private final Uri uri;
        private final File file;
        private final OutputStream output;
        private final boolean mediaStore;
        private long written;

        private BlobSession(
                String filename,
                String mimeType,
                long expectedSize,
                Uri uri,
                File file,
                OutputStream output,
                boolean mediaStore) {
            this.filename = filename;
            this.mimeType = mimeType;
            this.expectedSize = expectedSize;
            this.uri = uri;
            this.file = file;
            this.output = output;
            this.mediaStore = mediaStore;
        }
    }
}
