import UIKit
import WebKit

final class ConsoleViewController: UIViewController {
    private let serverURL: URL
    private var webView: WKWebView!
    private var scriptProxy: WeakScriptMessageHandler!
    private var downloads: [ObjectIdentifier: URL] = [:]
    private var pendingRoute: String?
    private var backNavigationInFlight = false
    private var connectionAlertVisible = false

    init(serverURL: URL) {
        self.serverURL = serverURL
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = ConsolePalette.background
        configureWebView()
        configureBackGesture()
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(notificationStatusChanged),
            name: .codexNotificationStatusDidChange,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(openNotificationThread(_:)),
            name: .codexOpenNotificationThread,
            object: nil
        )
        pendingRoute = NotificationRoute.shared.pendingThreadId
        NotificationMonitor.shared.startForegroundPolling()
        loadConsole()
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        navigationController?.interactivePopGestureRecognizer?.isEnabled = false
        attemptPendingRoute()
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        navigationController?.interactivePopGestureRecognizer?.isEnabled = true
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
        webView?.configuration.userContentController.removeScriptMessageHandler(forName: "codexNative")
    }

    override func accessibilityPerformEscape() -> Bool {
        navigateBack()
        return true
    }

    private func configureWebView() {
        let controller = WKUserContentController()
        scriptProxy = WeakScriptMessageHandler(delegate: self)
        controller.add(scriptProxy, name: "codexNative")

        let hasStoredToken: Bool
        if let credentials = CredentialsStore.shared.load(),
           let storedServer = ServerAddress.normalize(credentials.server),
           ServerAddress.sameOrigin(storedServer, serverURL) {
            hasStoredToken = credentials.token != nil
        } else {
            hasStoredToken = false
        }
        controller.addUserScript(WKUserScript(
            source: nativeBridgeScript(configured: hasStoredToken),
            injectionTime: .atDocumentStart,
            forMainFrameOnly: true
        ))

        let configuration = WKWebViewConfiguration()
        configuration.userContentController = controller
        configuration.websiteDataStore = .default()
        configuration.applicationNameForUserAgent = "CodexLanConsole/ios"
        configuration.defaultWebpagePreferences.allowsContentJavaScript = true
        configuration.allowsInlineMediaPlayback = true
        configuration.mediaTypesRequiringUserActionForPlayback = []

        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.backgroundColor = ConsolePalette.background
        webView.isOpaque = false
        webView.scrollView.backgroundColor = ConsolePalette.background
        // A single custom edge gesture coordinates SPA navigation, WebKit history,
        // and the native server screen without accidentally moving back twice.
        webView.allowsBackForwardNavigationGestures = false
        webView.navigationDelegate = self
        webView.uiDelegate = self
        webView.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(webView)
        NSLayoutConstraint.activate([
            webView.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            webView.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            webView.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor),
            webView.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor)
        ])
        self.webView = webView
    }

    private func loadConsole() {
        let request = URLRequest(url: serverURL, cachePolicy: .useProtocolCachePolicy, timeoutInterval: 30)
        guard let credentials = CredentialsStore.shared.load(),
              let storedServer = ServerAddress.normalize(credentials.server),
              ServerAddress.sameOrigin(storedServer, serverURL),
              let token = credentials.token,
              token.range(of: "^[0-9A-Fa-f]{64}$", options: .regularExpression) != nil,
              let host = serverURL.host else {
            webView.load(request)
            return
        }

        var properties: [HTTPCookiePropertyKey: Any] = [
            .originURL: serverURL,
            .domain: host,
            .path: "/",
            .name: "CodexLanSession",
            .value: token,
            .expires: Date(timeIntervalSinceNow: 30 * 24 * 60 * 60),
            HTTPCookiePropertyKey(rawValue: "HttpOnly"): "TRUE"
        ]
        if serverURL.scheme?.lowercased() == "https" {
            properties[.secure] = "TRUE"
        }
        guard let cookie = HTTPCookie(properties: properties) else {
            webView.load(request)
            return
        }
        webView.configuration.websiteDataStore.httpCookieStore.setCookie(cookie) { [weak self] in
            self?.webView.load(request)
        }
    }

    private func configureBackGesture() {
        let gesture = UIScreenEdgePanGestureRecognizer(target: self, action: #selector(handleEdgeBack(_:)))
        gesture.edges = .left
        gesture.cancelsTouchesInView = false
        view.addGestureRecognizer(gesture)
    }

    @objc private func handleEdgeBack(_ gesture: UIScreenEdgePanGestureRecognizer) {
        guard gesture.state == .ended else { return }
        let translation = gesture.translation(in: view).x
        let velocity = gesture.velocity(in: view).x
        guard translation > 55 || velocity > 450 else { return }
        navigateBack()
    }

    private func navigateBack() {
        guard !backNavigationInFlight else { return }
        backNavigationInFlight = true
        let script = """
        (() => {
          const detail = document.querySelector('#threadDetail.page.active');
          if (detail && typeof window.CodexConsoleHandleBack === 'function') {
            window.CodexConsoleHandleBack();
            return 'handled';
          }
          return 'native';
        })()
        """
        webView.evaluateJavaScript(script) { [weak self] result, _ in
            guard let self else { return }
            self.backNavigationInFlight = false
            if result as? String == "handled" { return }
            if self.webView.canGoBack {
                self.webView.goBack()
            } else {
                self.navigationController?.popViewController(animated: true)
            }
        }
    }

    @objc private func notificationStatusChanged() {
        dispatchNotificationStatus()
    }

    @objc private func openNotificationThread(_ notification: Notification) {
        guard let threadId = notification.object as? String,
              ServerAddress.isSafeThreadIdentifier(threadId) else { return }
        pendingRoute = threadId
        attemptPendingRoute()
    }

    private func attemptPendingRoute() {
        guard let threadId = pendingRoute ?? NotificationRoute.shared.pendingThreadId,
              ServerAddress.isSafeThreadIdentifier(threadId),
              webView.url != nil else { return }
        let encoded = javaScriptJSON(threadId)
        let script = """
        (() => {
          const id = \(encoded);
          if (typeof window.openThread !== 'function') return false;
          Promise.resolve(window.openThread(id))
            .then(() => typeof window.refreshCurrentThread === 'function' ? window.refreshCurrentThread() : null)
            .then(() => window.webkit.messageHandlers.codexNative.postMessage({method:'routeResult',args:[id,true]}))
            .catch(() => window.webkit.messageHandlers.codexNative.postMessage({method:'routeResult',args:[id,false]}));
          return true;
        })()
        """
        webView.evaluateJavaScript(script)
    }

    private func dispatchNotificationStatus() {
        NotificationMonitor.shared.authorizationState { [weak self] permission in
            guard let self else { return }
            let credentials = CredentialsStore.shared.load()
            let configured: Bool
            if let storedServer = credentials.flatMap({ ServerAddress.normalize($0.server) }) {
                configured = credentials?.token != nil && ServerAddress.sameOrigin(storedServer, self.serverURL)
            } else {
                configured = false
            }
            let enabled = NotificationMonitor.shared.isEnabled
            let status: [String: Any] = [
                "supported": true,
                "configured": configured,
                "enabled": enabled,
                "permission": permission,
                "serviceRunning": configured && enabled && permission == "granted",
                "batteryOptimized": false,
                "backgroundRestricted": true,
                "manufacturer": "apple",
                "lastError": ""
            ]
            guard let data = try? JSONSerialization.data(withJSONObject: status),
                  let base64 = data.base64EncodedString().addingPercentEncoding(withAllowedCharacters: .alphanumerics) else {
                return
            }
            let script = """
            (() => {
              try {
                const status = JSON.parse(atob(decodeURIComponent('\(base64)')));
                if (typeof window.__codexSetNotificationStatus === 'function') {
                  window.__codexSetNotificationStatus(status);
                }
              } catch (_) {}
            })()
            """
            self.webView.evaluateJavaScript(script)
        }
    }

    private func nativeBridgeScript(configured: Bool) -> String {
        let initialStatus: [String: Any] = [
            "supported": true,
            "configured": configured,
            "enabled": NotificationMonitor.shared.isEnabled,
            "permission": "required",
            "serviceRunning": false,
            "batteryOptimized": false,
            "backgroundRestricted": true,
            "manufacturer": "apple",
            "lastError": ""
        ]
        let statusData = try? JSONSerialization.data(withJSONObject: initialStatus)
        let statusBase64 = statusData?.base64EncodedString() ?? "e30="
        return """
        (() => {
          const decode = value => { try { return atob(value); } catch (_) { return ''; } };
          let status = {};
          try { status = JSON.parse(decode('\(statusBase64)')); } catch (_) {}
          const post = (method, args = []) => {
            try { window.webkit.messageHandlers.codexNative.postMessage({method, args}); } catch (_) {}
          };
          window.__codexSetNotificationStatus = next => {
            status = Object.assign({}, status, next || {});
            window.dispatchEvent(new CustomEvent('codex-notification-status', {detail: status}));
          };
          window.CodexAndroidNotifications = Object.freeze({
            getStatus: () => JSON.stringify(status),
            configure: token => {
              if (!/^[0-9a-f]{64}$/i.test(String(token || ''))) return 'invalid_token';
              status.configured = true;
              post('configure', [String(token)]);
              return 'configured';
            },
            requestPermission: () => {
              post('requestPermission');
              return status.permission === 'granted' ? 'granted' : 'requested';
            },
            setEnabled: enabled => {
              status.enabled = Boolean(enabled);
              status.serviceRunning = status.enabled && status.configured && status.permission === 'granted';
              post('setEnabled', [Boolean(enabled)]);
              return enabled ? 'started' : 'stopped';
            },
            testNotification: () => { post('testNotification'); return 'sent'; },
            openSettings: () => { post('openSettings'); return 'opened'; },
            acknowledgeThreadOpen: (threadId, attempt, succeeded) => {
              post('acknowledgeThreadOpen', [threadId, attempt, Boolean(succeeded)]);
            }
          });
        })();
        """
    }

    private func javaScriptJSON(_ value: String) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: [value]),
              let array = String(data: data, encoding: .utf8),
              array.count >= 2 else { return "\"\"" }
        return String(array.dropFirst().dropLast())
    }

    private func isTrustedMessage(_ message: WKScriptMessage) -> Bool {
        guard message.frameInfo.isMainFrame else { return false }
        let origin = message.frameInfo.securityOrigin
        guard origin.protocol.lowercased() == serverURL.scheme?.lowercased(),
              origin.host.lowercased() == serverURL.host?.lowercased() else { return false }
        let expectedPort = serverURL.port ?? (serverURL.scheme?.lowercased() == "https" ? 443 : 80)
        let actualPort = origin.port == 0
            ? (origin.protocol.lowercased() == "https" ? 443 : 80)
            : origin.port
        return actualPort == expectedPort
    }

    private func showConnectionError(_ error: Error) {
        guard !connectionAlertVisible, view.window != nil else { return }
        connectionAlertVisible = true
        let alert = UIAlertController(
            title: "无法连接电脑",
            message: error.localizedDescription,
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            self?.connectionAlertVisible = false
            self?.loadConsole()
        })
        alert.addAction(UIAlertAction(title: "更换地址", style: .cancel) { [weak self] _ in
            self?.connectionAlertVisible = false
            self?.navigationController?.popViewController(animated: true)
        })
        present(alert, animated: true)
    }

    private func openExternal(_ url: URL) {
        UIApplication.shared.open(url)
    }

    private func presentDownloadedFile(_ url: URL) {
        guard view.window != nil else { return }
        let activity = UIActivityViewController(activityItems: [url], applicationActivities: nil)
        activity.completionWithItemsHandler = { _, _, _, _ in
            try? FileManager.default.removeItem(at: url)
        }
        if let popover = activity.popoverPresentationController {
            popover.sourceView = view
            popover.sourceRect = CGRect(x: view.bounds.midX, y: view.bounds.maxY - 40, width: 1, height: 1)
        }
        present(activity, animated: true)
    }
}

extension ConsoleViewController: WKScriptMessageHandler {
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == "codexNative",
              isTrustedMessage(message),
              let payload = message.body as? [String: Any],
              let method = payload["method"] as? String else { return }
        let args = payload["args"] as? [Any] ?? []
        switch method {
        case "configure":
            guard let token = args.first as? String,
                  token.range(of: "^[0-9A-Fa-f]{64}$", options: .regularExpression) != nil else { return }
            do {
                try CredentialsStore.shared.saveToken(token, for: serverURL)
                NotificationMonitor.shared.credentialsDidChange(server: serverURL)
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
                    self?.attemptPendingRoute()
                }
            } catch {
                return
            }
        case "requestPermission":
            NotificationMonitor.shared.requestPermission()
        case "setEnabled":
            guard let enabled = args.first as? Bool else { return }
            NotificationMonitor.shared.setEnabled(enabled)
        case "testNotification":
            NotificationMonitor.shared.sendTestNotification()
        case "openSettings":
            guard let url = URL(string: UIApplication.openSettingsURLString) else { return }
            UIApplication.shared.open(url)
        case "routeResult":
            guard args.count >= 2,
                  let threadId = args[0] as? String,
                  let succeeded = args[1] as? Bool,
                  succeeded,
                  threadId == pendingRoute || threadId == NotificationRoute.shared.pendingThreadId else { return }
            pendingRoute = nil
            NotificationRoute.shared.pendingThreadId = nil
        case "acknowledgeThreadOpen":
            break
        default:
            break
        }
        dispatchNotificationStatus()
    }
}

extension ConsoleViewController: WKNavigationDelegate {
    func webView(
        _ webView: WKWebView,
        decidePolicyFor navigationAction: WKNavigationAction,
        decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
    ) {
        guard let url = navigationAction.request.url else {
            decisionHandler(.cancel)
            return
        }
        let scheme = url.scheme?.lowercased() ?? ""
        if scheme == "http" || scheme == "https" {
            if let rewritten = ServerAddress.rewritingLoopback(url, through: serverURL) {
                decisionHandler(.cancel)
                // Open mapped developer services outside the authenticated console
                // WebView so its port-agnostic session cookie cannot leak to them.
                openExternal(rewritten)
                return
            }
            if ServerAddress.sameOrigin(url, serverURL) {
                decisionHandler(navigationAction.shouldPerformDownload ? .download : .allow)
            } else {
                decisionHandler(.cancel)
                openExternal(url)
            }
            return
        }
        if ["mailto", "tel", "sms", "facetime", "facetime-audio"].contains(scheme) {
            decisionHandler(.cancel)
            openExternal(url)
            return
        }
        if scheme == "blob" && navigationAction.shouldPerformDownload {
            decisionHandler(.download)
        } else {
            decisionHandler(scheme == "about" || scheme == "blob" ? .allow : .cancel)
        }
    }

    func webView(
        _ webView: WKWebView,
        decidePolicyFor navigationResponse: WKNavigationResponse,
        decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void
    ) {
        let disposition = (navigationResponse.response as? HTTPURLResponse)?
            .value(forHTTPHeaderField: "Content-Disposition")?
            .lowercased() ?? ""
        if navigationResponse.canShowMIMEType && !disposition.contains("attachment") {
            decisionHandler(.allow)
        } else {
            decisionHandler(.download)
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        connectionAlertVisible = false
        dispatchNotificationStatus()
        attemptPendingRoute()
    }

    func webView(
        _ webView: WKWebView,
        didFailProvisionalNavigation navigation: WKNavigation!,
        withError error: Error
    ) {
        showConnectionError(error)
    }

    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }
}

extension ConsoleViewController: WKUIDelegate {
    func webView(
        _ webView: WKWebView,
        createWebViewWith configuration: WKWebViewConfiguration,
        for navigationAction: WKNavigationAction,
        windowFeatures: WKWindowFeatures
    ) -> WKWebView? {
        if let url = navigationAction.request.url {
            webView.load(URLRequest(url: url))
        }
        return nil
    }

    func webView(
        _ webView: WKWebView,
        runJavaScriptAlertPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping () -> Void
    ) {
        let alert = UIAlertController(title: "Codex LAN", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .default) { _ in completionHandler() })
        present(alert, animated: true)
    }

    func webView(
        _ webView: WKWebView,
        runJavaScriptConfirmPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping (Bool) -> Void
    ) {
        let alert = UIAlertController(title: "请确认", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { _ in completionHandler(false) })
        alert.addAction(UIAlertAction(title: "确定", style: .default) { _ in completionHandler(true) })
        present(alert, animated: true)
    }
}

extension ConsoleViewController: WKDownloadDelegate {
    func download(
        _ download: WKDownload,
        decideDestinationUsing response: URLResponse,
        suggestedFilename: String,
        completionHandler: @escaping (URL?) -> Void
    ) {
        let cleanedName = suggestedFilename
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "\\", with: "_")
        let safeName = String(cleanedName.prefix(160))
        let destination = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(UUID().uuidString)-\(safeName.isEmpty ? "download" : safeName)")
        downloads[ObjectIdentifier(download)] = destination
        completionHandler(destination)
    }

    func downloadDidFinish(_ download: WKDownload) {
        guard let url = downloads.removeValue(forKey: ObjectIdentifier(download)) else { return }
        presentDownloadedFile(url)
    }

    func download(_ download: WKDownload, didFailWithError error: Error, resumeData: Data?) {
        if let url = downloads.removeValue(forKey: ObjectIdentifier(download)) {
            try? FileManager.default.removeItem(at: url)
        }
        let alert = UIAlertController(title: "下载失败", message: error.localizedDescription, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .default))
        present(alert, animated: true)
    }
}

private final class WeakScriptMessageHandler: NSObject, WKScriptMessageHandler {
    weak var delegate: WKScriptMessageHandler?

    init(delegate: WKScriptMessageHandler) {
        self.delegate = delegate
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        delegate?.userContentController(userContentController, didReceive: message)
    }
}
