import BackgroundTasks
import Foundation
import UIKit
import UserNotifications

extension Notification.Name {
    static let codexNotificationStatusDidChange = Notification.Name("codexNotificationStatusDidChange")
}

final class NotificationMonitor {
    static let shared = NotificationMonitor()
    static let refreshIdentifier = "local.codex.lanconsole.ios.notification-refresh"

    private let defaults = UserDefaults.standard
    private let enabledKey = "notification-monitor-enabled"
    private let cursorKey = "notification-monitor-cursor"
    private let serverKey = "notification-monitor-server"
    private let permissionGrantedKey = "notification-monitor-permission-granted"
    private let stateQueue = DispatchQueue(label: "local.codex.lanconsole.notification-state")
    private var foregroundTimer: Timer?

    private init() {}

    var isEnabled: Bool {
        defaults.bool(forKey: enabledKey)
    }

    func registerBackgroundTask() {
        BGTaskScheduler.shared.register(
            forTaskWithIdentifier: Self.refreshIdentifier,
            using: nil
        ) { [weak self] task in
            guard let self, let refreshTask = task as? BGAppRefreshTask else {
                task.setTaskCompleted(success: false)
                return
            }
            self.handle(refreshTask)
        }
    }

    func credentialsDidChange(server: URL) {
        let normalized = server.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        stateQueue.sync {
            if defaults.string(forKey: serverKey) != normalized {
                defaults.set(normalized, forKey: serverKey)
                defaults.removeObject(forKey: cursorKey)
            }
        }
        postStatusChange()
    }

    func setEnabled(_ enabled: Bool) {
        defaults.set(enabled, forKey: enabledKey)
        if enabled {
            authorizationState { permission in
                guard permission == "granted", self.isEnabled else { return }
                self.startForegroundPolling()
                self.scheduleBackgroundRefresh()
            }
        } else {
            stopForegroundPolling()
            BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: Self.refreshIdentifier)
        }
        postStatusChange()
    }

    func requestPermission(completion: ((Bool) -> Void)? = nil) {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .badge, .sound]) { granted, _ in
            DispatchQueue.main.async {
                self.defaults.set(granted, forKey: self.permissionGrantedKey)
                if granted && self.isEnabled {
                    self.startForegroundPolling()
                    self.scheduleBackgroundRefresh()
                }
                self.postStatusChange()
                completion?(granted)
            }
        }
    }

    func authorizationState(completion: @escaping (String) -> Void) {
        UNUserNotificationCenter.current().getNotificationSettings { settings in
            let state: String
            switch settings.authorizationStatus {
            case .authorized, .provisional, .ephemeral:
                state = "granted"
            case .denied:
                state = "blocked"
            case .notDetermined:
                state = "required"
            @unknown default:
                state = "required"
            }
            DispatchQueue.main.async {
                self.defaults.set(state == "granted", forKey: self.permissionGrantedKey)
                if state == "granted" && self.isEnabled {
                    self.startForegroundPolling()
                } else if state != "granted" {
                    self.stopForegroundPolling()
                }
                completion(state)
            }
        }
    }

    func sendTestNotification() {
        let content = UNMutableNotificationContent()
        content.title = "Codex 通知测试"
        content.body = "任务完成或需要决定时，会通过系统通知提醒你。"
        content.sound = .default
        let request = UNNotificationRequest(
            identifier: "codex-test-\(UUID().uuidString)",
            content: content,
            trigger: UNTimeIntervalNotificationTrigger(timeInterval: 1, repeats: false)
        )
        UNUserNotificationCenter.current().add(request)
    }

    func scheduleBackgroundRefresh() {
        guard isEnabled,
              defaults.bool(forKey: permissionGrantedKey),
              CredentialsStore.shared.load()?.token != nil else { return }
        BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: Self.refreshIdentifier)
        let request = BGAppRefreshTaskRequest(identifier: Self.refreshIdentifier)
        request.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(request)
    }

    func startForegroundPolling() {
        guard isEnabled, defaults.bool(forKey: permissionGrantedKey) else { return }
        DispatchQueue.main.async {
            guard self.foregroundTimer == nil else { return }
            self.poll { _ in }
            self.foregroundTimer = Timer.scheduledTimer(withTimeInterval: 25, repeats: true) { [weak self] _ in
                self?.poll { _ in }
            }
        }
    }

    func stopForegroundPolling() {
        DispatchQueue.main.async {
            self.foregroundTimer?.invalidate()
            self.foregroundTimer = nil
        }
    }

    @discardableResult
    func poll(completion: @escaping (Bool) -> Void) -> URLSessionDataTask? {
        guard isEnabled,
              defaults.bool(forKey: permissionGrantedKey),
              let credentials = CredentialsStore.shared.load(),
              let token = credentials.token,
              let server = ServerAddress.normalize(credentials.server),
              var components = URLComponents(
                url: server.appendingPathComponent("api/notifications/events"),
                resolvingAgainstBaseURL: false
              ) else {
            completion(false)
            return nil
        }

        let cursor = stateQueue.sync { defaults.object(forKey: cursorKey) as? NSNumber }
        var query = [
            URLQueryItem(name: "limit", value: "100")
        ]
        if let cursor {
            query.append(URLQueryItem(name: "after", value: cursor.stringValue))
        }
        components.queryItems = query
        guard let url = components.url else {
            completion(false)
            return nil
        }

        var request = URLRequest(url: url)
        request.timeoutInterval = 20
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")

        let task = URLSession.shared.dataTask(with: request) { data, response, error in
            guard error == nil,
                  let response = response as? HTTPURLResponse,
                  response.statusCode == 200,
                  let data,
                  let page = try? JSONDecoder().decode(NotificationPage.self, from: data) else {
                DispatchQueue.main.async { completion(false) }
                return
            }
            self.deliver(page, bootstrap: cursor == nil, completion: completion)
        }
        task.resume()
        return task
    }

    private func handle(_ task: BGAppRefreshTask) {
        scheduleBackgroundRefresh()
        let gate = CompletionGate(task: task)
        let networkTask = poll { success in
            gate.finish(success: success)
        }
        task.expirationHandler = {
            networkTask?.cancel()
            gate.finish(success: false)
        }
    }

    private func deliver(
        _ page: NotificationPage,
        bootstrap: Bool,
        completion: @escaping (Bool) -> Void
    ) {
        let events = page.events.filter { !bootstrap || $0.requiresAction }
        guard !events.isEmpty else {
            saveCursor(page.nextCursor)
            DispatchQueue.main.async { completion(true) }
            return
        }

        let group = DispatchGroup()
        let failureLock = NSLock()
        var failed = false
        for event in events {
            let content = UNMutableNotificationContent()
            content.title = event.title
            content.body = event.body
            content.sound = .default
            content.categoryIdentifier = event.requiresAction ? "CODEX_ACTION_REQUIRED" : "CODEX_TASK_STATUS"
            if let threadId = event.threadId, ServerAddress.isSafeThreadIdentifier(threadId) {
                content.userInfo["threadId"] = threadId
            }
            let request = UNNotificationRequest(
                identifier: "codex-event-\(event.id)",
                content: content,
                trigger: nil
            )
            group.enter()
            UNUserNotificationCenter.current().add(request) { error in
                if error != nil {
                    failureLock.lock()
                    failed = true
                    failureLock.unlock()
                }
                group.leave()
            }
        }
        group.notify(queue: .main) {
            failureLock.lock()
            let succeeded = !failed
            failureLock.unlock()
            if succeeded { self.saveCursor(page.nextCursor) }
            completion(succeeded)
        }
    }

    private func saveCursor(_ cursor: Int64) {
        stateQueue.sync {
            defaults.set(NSNumber(value: cursor), forKey: cursorKey)
        }
    }

    private func postStatusChange() {
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: .codexNotificationStatusDidChange, object: nil)
        }
    }
}

private struct NotificationPage: Decodable {
    let events: [CodexNotificationEvent]
    let nextCursor: Int64
}

private struct CodexNotificationEvent: Decodable {
    let id: Int64
    let threadId: String?
    let title: String
    let body: String
    let requiresAction: Bool
}

private final class CompletionGate {
    private let lock = NSLock()
    private var finished = false
    private weak var task: BGTask?

    init(task: BGTask) {
        self.task = task
    }

    func finish(success: Bool) {
        lock.lock()
        guard !finished else {
            lock.unlock()
            return
        }
        finished = true
        let task = self.task
        lock.unlock()
        task?.setTaskCompleted(success: success)
    }
}
