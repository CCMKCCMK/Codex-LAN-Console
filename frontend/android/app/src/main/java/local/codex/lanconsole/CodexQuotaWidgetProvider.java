package local.codex.lanconsole;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.widget.RemoteViews;

/** Fixed-size, transparent launcher widget for the paired Codex quota. */
public final class CodexQuotaWidgetProvider extends AppWidgetProvider {
    static final String ACTION_REFRESH = "local.codex.lanconsole.quota.REFRESH";

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent == null ? "" : intent.getAction();
        if (ACTION_REFRESH.equals(action)) {
            CodexQuotaWidgetUpdater.renderCached(context);
            BroadcastReceiver.PendingResult pending = goAsync();
            CodexQuotaWidgetUpdater.requestRefresh(context, true, pending::finish);
            return;
        }
        if (AppWidgetManager.ACTION_APPWIDGET_UPDATE.equals(action)) {
            CodexQuotaWidgetUpdater.renderCached(context);
            BroadcastReceiver.PendingResult pending = goAsync();
            CodexQuotaWidgetUpdater.requestRefresh(context, false, pending::finish);
            return;
        }
        super.onReceive(context, intent);
    }

    @Override
    public void onEnabled(Context context) {
        CodexQuotaWidgetUpdater.renderCached(context);
        CodexQuotaWidgetUpdater.requestRefresh(context, true, null);
    }

    @Override
    public void onAppWidgetOptionsChanged(
            Context context,
            AppWidgetManager manager,
            int appWidgetId,
            android.os.Bundle newOptions) {
        CodexQuotaWidgetUpdater.renderCached(context);
    }

    static RemoteViews attachActions(Context context, RemoteViews views) {
        Intent open = new Intent(context, MainActivity.class)
                .putExtra(CodexNotificationService.EXTRA_OPEN_CONSOLE, true)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent openIntent = PendingIntent.getActivity(
                context,
                43101,
                open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(R.id.quota_widget_root, openIntent);

        Intent refresh = new Intent(context, CodexQuotaWidgetProvider.class)
                .setAction(ACTION_REFRESH);
        PendingIntent refreshIntent = PendingIntent.getBroadcast(
                context,
                43102,
                refresh,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        views.setOnClickPendingIntent(R.id.quota_refresh, refreshIntent);
        return views;
    }
}
