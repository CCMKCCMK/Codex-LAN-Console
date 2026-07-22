package local.codex.lanconsole;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Restores an explicitly enabled monitor after reboot or an in-place APK update. */
public final class NotificationBootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent == null ? "" : intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action)
                && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)) {
            return;
        }
        if (NotificationConfigStore.isEnabled(context)
                && NotificationConfigStore.isConfigured(context)
                && CodexNotificationService.hasNotificationPermission(context)) {
            CodexNotificationService.start(context);
        }
    }
}
