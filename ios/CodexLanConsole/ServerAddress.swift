import Foundation

enum ServerAddress {
    static func normalize(_ rawValue: String) -> URL? {
        var value = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return nil }
        if !value.lowercased().hasPrefix("http://") && !value.lowercased().hasPrefix("https://") {
            value = "http://" + value
        }
        guard var components = URLComponents(string: value),
              let scheme = components.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = components.host,
              !host.isEmpty,
              host.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
              host.rangeOfCharacter(from: .controlCharacters) == nil,
              components.user == nil,
              components.password == nil else {
            return nil
        }
        components.scheme = scheme
        components.query = nil
        components.fragment = nil
        components.path = ""
        return components.url
    }

    static func sameOrigin(_ lhs: URL, _ rhs: URL) -> Bool {
        lhs.scheme?.lowercased() == rhs.scheme?.lowercased()
            && lhs.host?.lowercased() == rhs.host?.lowercased()
            && effectivePort(lhs) == effectivePort(rhs)
    }

    static func isLoopback(_ host: String?) -> Bool {
        guard let host = host?.lowercased() else { return false }
        return host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]"
    }

    static func rewritingLoopback(_ target: URL, through server: URL) -> URL? {
        guard isLoopback(target.host), var components = URLComponents(url: target, resolvingAgainstBaseURL: false) else {
            return nil
        }
        components.scheme = server.scheme
        components.host = server.host
        if target.port == nil {
            components.port = server.port
        }
        return components.url
    }

    static func isSafeThreadIdentifier(_ value: String) -> Bool {
        guard !value.isEmpty, value.count <= 200 else { return false }
        return value.unicodeScalars.allSatisfy { scalar in
            let code = scalar.value
            return (48...57).contains(code)
                || (65...90).contains(code)
                || (97...122).contains(code)
                || [46, 95, 58, 47, 45].contains(code)
        }
    }

    private static func effectivePort(_ url: URL) -> Int {
        if let port = url.port { return port }
        return url.scheme?.lowercased() == "https" ? 443 : 80
    }
}
