import Foundation
import Security

struct StoredCredentials: Codable, Equatable {
    let server: String
    let token: String?
}

enum CredentialsStoreError: LocalizedError {
    case encodingFailed
    case keychain(OSStatus)

    var errorDescription: String? {
        switch self {
        case .encodingFailed:
            return "The connection details could not be encoded."
        case let .keychain(status):
            return "Keychain operation failed (\(status))."
        }
    }
}
final class CredentialsStore {
    static let shared = CredentialsStore()

    private let service = "local.codex.lanconsole.ios.credentials"
    private let account = "active-bridge"
    private let lock = NSLock()

    private init() {}

    func load() -> StoredCredentials? {
        lock.lock()
        defer { lock.unlock() }
        return loadUnlocked()
    }

    @discardableResult
    func saveServer(_ server: URL) throws -> StoredCredentials {
        lock.lock()
        defer { lock.unlock() }
        let normalized = server.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let existing = loadUnlocked()
        let preservedToken: String?
        if let existing,
           let oldServer = ServerAddress.normalize(existing.server),
           ServerAddress.sameOrigin(oldServer, server) {
            preservedToken = existing.token
        } else {
            preservedToken = nil
        }
        let credentials = StoredCredentials(server: normalized, token: preservedToken)
        try writeUnlocked(credentials)
        return credentials
    }

    func saveToken(_ token: String, for server: URL) throws {
        guard token.range(of: "^[0-9A-Fa-f]{64}$", options: .regularExpression) != nil else {
            return
        }
        lock.lock()
        defer { lock.unlock() }
        let normalized = server.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        try writeUnlocked(StoredCredentials(server: normalized, token: token))
    }

    func clearToken() throws {
        lock.lock()
        defer { lock.unlock() }
        guard let current = loadUnlocked() else { return }
        try writeUnlocked(StoredCredentials(server: current.server, token: nil))
    }

    private func loadUnlocked() -> StoredCredentials? {
        var query = baseQuery()
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(StoredCredentials.self, from: data)
    }

    private func writeUnlocked(_ credentials: StoredCredentials) throws {
        guard let data = try? JSONEncoder().encode(credentials) else {
            throw CredentialsStoreError.encodingFailed
        }
        let base = baseQuery()
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        ]
        let updateStatus = SecItemUpdate(base as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else {
            throw CredentialsStoreError.keychain(updateStatus)
        }
        var insert = base
        attributes.forEach { insert[$0.key] = $0.value }
        let insertStatus = SecItemAdd(insert as CFDictionary, nil)
        guard insertStatus == errSecSuccess else {
            throw CredentialsStoreError.keychain(insertStatus)
        }
    }

    private func baseQuery() -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
    }
}
