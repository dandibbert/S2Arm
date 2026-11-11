import XCTest
@testable import NX2AppleUI

// 手柄弹窗标题照连接程序报的型号写（#520）。期望按 ns2_kind 数值手写：1 Pro、2 Joy-Con 左、3 右、4 左右一对；
//   没连 / 旧连接程序没报 kind 时写通用的 Switch 手柄 / Switch Controller
final class NS2LauncherControllerTests: XCTestCase {
    func testTitleFollowsKind() {
        XCTAssertEqual(NS2LauncherController.title(kind: 2, simplifiedChinese: true), "Joy-Con 2 (L)")
        XCTAssertEqual(NS2LauncherController.title(kind: 3, simplifiedChinese: false), "Joy-Con 2 (R)")
        XCTAssertEqual(NS2LauncherController.title(kind: 4, simplifiedChinese: true), "Joy-Con 2 (L + R)")
        XCTAssertEqual(NS2LauncherController.title(kind: 1, simplifiedChinese: true), "Switch 2 Pro")
        XCTAssertEqual(NS2LauncherController.title(kind: 0, simplifiedChinese: true), "Switch 手柄")
        XCTAssertEqual(NS2LauncherController.title(kind: 0, simplifiedChinese: false), "Switch Controller")
    }

    func testKindFromStatusLine() throws {
        let line = #"{"status":4,"message":"","name":"Joy-Con 2 (R)","identifier":"x","kind":3}"#
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any])
        XCTAssertEqual(NS2LauncherController.kind(from: object), 3)
        XCTAssertEqual(NS2LauncherController.kind(from: ["status": 4]), 0, "旧连接程序没报 kind")
    }
}
