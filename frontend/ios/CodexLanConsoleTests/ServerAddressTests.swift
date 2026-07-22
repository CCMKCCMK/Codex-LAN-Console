import XCTest
@testable import CodexLanConsole

final class ServerAddressTests: XCTestCase {
    func testAddsHTTPWhenSchemeIsMissing() {
        XCTAssertEqual(
            ServerAddress.normalize("100.64.0.10:8787")?.absoluteString,
            "http://100.64.0.10:8787"
        )
    }

    func testNormalizesRootAndDropsQueryData() {
        XCTAssertEqual(
            ServerAddress.normalize(" https://example.test:8787/path?secret=value ")?.absoluteString,
            "https://example.test:8787"
        )
    }

    func testRejectsUnsupportedSchemesAndEmbeddedCredentials() {
        XCTAssertNil(ServerAddress.normalize("file:///tmp/bridge"))
        XCTAssertNil(ServerAddress.normalize("http://name:password@example.test:8787"))
        XCTAssertNil(ServerAddress.normalize("not a host"))
    }

    func testSameOriginUsesDefaultPorts() {
        let first = URL(string: "https://example.test")!
        let second = URL(string: "https://example.test:443/path")!
        XCTAssertTrue(ServerAddress.sameOrigin(first, second))
        XCTAssertFalse(ServerAddress.sameOrigin(first, URL(string: "http://example.test")!))
    }

    func testRewritesLoopbackToRemoteHostAndKeepsServicePort() {
        let target = URL(string: "http://127.0.0.1:3000/app?q=1")!
        let server = URL(string: "http://100.64.0.10:8787")!
        XCTAssertEqual(
            ServerAddress.rewritingLoopback(target, through: server)?.absoluteString,
            "http://100.64.0.10:3000/app?q=1"
        )
    }

    func testThreadIdentifierValidation() {
        XCTAssertTrue(ServerAddress.isSafeThreadIdentifier("thread/019f6578-b98b:turn_2"))
        XCTAssertFalse(ServerAddress.isSafeThreadIdentifier("../../thread?token=secret"))
        XCTAssertFalse(ServerAddress.isSafeThreadIdentifier("任务/123"))
        XCTAssertFalse(ServerAddress.isSafeThreadIdentifier(String(repeating: "a", count: 201)))
    }
}
