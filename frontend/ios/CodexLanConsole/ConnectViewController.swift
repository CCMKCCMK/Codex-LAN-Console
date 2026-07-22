import UIKit

final class ConnectViewController: UIViewController, UITextFieldDelegate {
    private let serverField = UITextField()
    private let errorLabel = UILabel()
    private let connectButton = UIButton(type: .system)

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = ConsolePalette.background
        buildInterface()
        serverField.text = CredentialsStore.shared.load()?.server
        if serverField.text?.isEmpty != false {
            serverField.placeholder = "http://100.x.x.x:8787"
        }
    }

    private func buildInterface() {
        let logo = UILabel()
        logo.text = "C"
        logo.textAlignment = .center
        logo.textColor = ConsolePalette.background
        logo.backgroundColor = ConsolePalette.accent
        logo.font = .systemFont(ofSize: 28, weight: .bold)
        logo.layer.cornerRadius = 24
        logo.clipsToBounds = true
        logo.translatesAutoresizingMaskIntoConstraints = false

        let title = UILabel()
        title.text = "Codex LAN Console"
        title.textColor = .white
        title.font = .systemFont(ofSize: 28, weight: .bold)
        title.textAlignment = .center

        let subtitle = UILabel()
        subtitle.text = "输入电脑的局域网或 Tailscale 地址。应用本身不需要云端账号。"
        subtitle.textColor = ConsolePalette.secondaryText
        subtitle.font = .systemFont(ofSize: 15)
        subtitle.numberOfLines = 0
        subtitle.textAlignment = .center

        serverField.textColor = .white
        serverField.tintColor = ConsolePalette.accent
        serverField.backgroundColor = ConsolePalette.background
        serverField.borderStyle = .roundedRect
        serverField.autocorrectionType = .no
        serverField.autocapitalizationType = .none
        serverField.keyboardType = .URL
        serverField.returnKeyType = .go
        serverField.clearButtonMode = .whileEditing
        serverField.delegate = self
        serverField.accessibilityLabel = "电脑地址"

        connectButton.setTitle("连接电脑", for: .normal)
        connectButton.setTitleColor(ConsolePalette.background, for: .normal)
        connectButton.titleLabel?.font = .systemFont(ofSize: 17, weight: .semibold)
        connectButton.backgroundColor = ConsolePalette.accent
        connectButton.layer.cornerRadius = 12
        connectButton.heightAnchor.constraint(equalToConstant: 52).isActive = true
        connectButton.addTarget(self, action: #selector(connect), for: .touchUpInside)

        errorLabel.textColor = UIColor(red: 1, green: 0.48, blue: 0.48, alpha: 1)
        errorLabel.font = .systemFont(ofSize: 13)
        errorLabel.numberOfLines = 0
        errorLabel.textAlignment = .center
        errorLabel.isHidden = true

        let note = UILabel()
        note.text = "建议使用 Tailscale 地址。普通 HTTP 只应在可信局域网或加密的 Tailscale 网络中使用。"
        note.textColor = ConsolePalette.secondaryText
        note.font = .systemFont(ofSize: 12)
        note.numberOfLines = 0
        note.textAlignment = .center

        let stack = UIStackView(arrangedSubviews: [logo, title, subtitle, serverField, connectButton, errorLabel, note])
        stack.axis = .vertical
        stack.alignment = .fill
        stack.spacing = 16
        stack.setCustomSpacing(24, after: subtitle)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let card = UIView()
        card.backgroundColor = ConsolePalette.card
        card.layer.cornerRadius = 22
        card.translatesAutoresizingMaskIntoConstraints = false
        card.addSubview(stack)
        view.addSubview(card)

        NSLayoutConstraint.activate([
            logo.widthAnchor.constraint(equalToConstant: 48),
            logo.heightAnchor.constraint(equalToConstant: 48),
            card.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            card.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24),
            card.centerYAnchor.constraint(equalTo: view.safeAreaLayoutGuide.centerYAnchor),
            stack.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: card.topAnchor, constant: 28),
            stack.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -28)
        ])
        logo.setContentHuggingPriority(.required, for: .horizontal)
    }

    @objc private func connect() {
        errorLabel.isHidden = true
        guard let value = serverField.text, let server = ServerAddress.normalize(value) else {
            showError("请输入有效的 http 或 https 地址，例如 http://100.x.x.x:8787")
            return
        }
        do {
            try CredentialsStore.shared.saveServer(server)
            view.endEditing(true)
            navigationController?.pushViewController(ConsoleViewController(serverURL: server), animated: true)
        } catch {
            showError("无法安全保存电脑地址：\(error.localizedDescription)")
        }
    }

    private func showError(_ message: String) {
        errorLabel.text = message
        errorLabel.isHidden = false
        UIAccessibility.post(notification: .announcement, argument: message)
    }

    func textFieldShouldReturn(_ textField: UITextField) -> Bool {
        connect()
        return true
    }
}
