import UIKit

final class SceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    func scene(
        _ scene: UIScene,
        willConnectTo session: UISceneSession,
        options connectionOptions: UIScene.ConnectionOptions
    ) {
        guard let windowScene = scene as? UIWindowScene else { return }
        let navigation = UINavigationController(rootViewController: ConnectViewController())
        navigation.setNavigationBarHidden(true, animated: false)
        navigation.view.backgroundColor = ConsolePalette.background

        let window = UIWindow(windowScene: windowScene)
        window.rootViewController = navigation
        window.backgroundColor = ConsolePalette.background
        window.makeKeyAndVisible()
        self.window = window

        if let server = CredentialsStore.shared.load()?.server,
           let url = ServerAddress.normalize(server) {
            navigation.pushViewController(ConsoleViewController(serverURL: url), animated: false)
        }
    }

    func sceneDidEnterBackground(_ scene: UIScene) {
        NotificationMonitor.shared.scheduleBackgroundRefresh()
    }
}
