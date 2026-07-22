import BackgroundTasks
import UIKit
import UserNotifications

@main
final class AppDelegate: UIResponder, UIApplicationDelegate, UNUserNotificationCenterDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        UNUserNotificationCenter.current().delegate = self
        NotificationMonitor.shared.registerBackgroundTask()
        return true
    }

    func application(
        _ application: UIApplication,
        configurationForConnecting connectingSceneSession: UISceneSession,
        options: UIScene.ConnectionOptions
    ) -> UISceneConfiguration {
        let configuration = UISceneConfiguration(
            name: "Default Configuration",
            sessionRole: connectingSceneSession.role
        )
        configuration.delegateClass = SceneDelegate.self
        return configuration
    }

    func applicationDidEnterBackground(_ application: UIApplication) {
        NotificationMonitor.shared.scheduleBackgroundRefresh()
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list, .sound])
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let threadId = response.notification.request.content.userInfo["threadId"] as? String
        if let threadId, ServerAddress.isSafeThreadIdentifier(threadId) {
            NotificationRoute.shared.pendingThreadId = threadId
            NotificationCenter.default.post(name: .codexOpenNotificationThread, object: threadId)
        }
        completionHandler()
    }
}

extension Notification.Name {
    static let codexOpenNotificationThread = Notification.Name("codexOpenNotificationThread")
}

final class NotificationRoute {
    static let shared = NotificationRoute()
    var pendingThreadId: String?
    private init() {}
}
