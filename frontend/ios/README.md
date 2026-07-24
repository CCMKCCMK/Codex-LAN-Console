# Codex LAN Console for iOS

This is an iOS shell for the existing Codex LAN Bridge. It uses the same browser interface and API as the Android client, so task lists, messages, attachments, files, tools, skills, permissions, and approvals stay on the Windows bridge.

## 中文快速说明

这个项目不包含任何人的 Apple 签名。GitHub 会产出一个模拟器包和一个供后续签名的未签名 IPA，但两者都不能直接安装到真机。最稳妥的安装方式是在 Mac 上用 Xcode 打开源码，在 `Signing & Capabilities` 中选择使用者自己的 Team，然后连接 iPhone 运行。详细步骤见下文。

## What is included

- Native `WKWebView` shell with iPhone and iPad safe-area handling.
- Left-edge back gesture for task details, browser history, and returning to the server screen.
- Automatic rewriting of `localhost`, `127.0.0.1`, and `::1` links to the selected remote computer address.
- Standard iOS document, photo, and video selection through HTML file inputs.
- Native WebKit downloads followed by the iOS Share sheet.
- Server address and 64-character pairing token stored in the iOS Keychain.
- Local notification permission, foreground polling, and best-effort `BGAppRefreshTask` polling.
- An unsigned GitHub Actions build and simulator test workflow.

## Build and self-sign on a Mac

Signing is intentionally not included. Apple requires the person installing the app to use their own Apple ID, development certificate, and provisioning profile.

1. Install Xcode 16 or newer on a Mac and open it once to finish installing components.
2. Install XcodeGen:

   ```sh
   brew install xcodegen
   ```

3. Clone or copy this repository, then generate the Xcode project:

   ```sh
   cd CodexLanConsole/frontend/ios
   xcodegen generate --spec project.yml
   open CodexLanConsole.xcodeproj
   ```

4. In Xcode Settings, add your Apple Account. Then select the `CodexLanConsole` target, open `Signing & Capabilities`, enable `Automatically manage signing`, select your own Team, and change the bundle identifier if Xcode says it is already in use.
5. Connect the iPhone, select it as the run destination, enable Developer Mode on the phone if requested, and press Run.
6. If iOS asks you to trust the developer certificate, follow the prompt under `Settings > General > VPN & Device Management`.

A free Apple ID normally creates a personal-team provisioning profile that expires after seven days. A paid Apple Developer membership supports normal development provisioning and distribution. These Apple rules are outside this project. See Apple's official guides for [running an app on a simulator or physical device](https://developer.apple.com/documentation/Xcode/running-your-app-on-simulated-or-physical-devices) and [developer account differences](https://developer.apple.com/help/account/basics/about-your-developer-account/).

## GitHub Actions artifacts

The `iOS unsigned build` workflow generates the Xcode project, runs the simulator tests, and uploads two deliberately unsigned artifacts:

- `Codex-LAN-Console-iOS-v1.6.0-simulator-unsigned.zip` is a simulator `.app` and only runs in the iOS Simulator.
- `Codex-LAN-Console-iOS-v1.6.0-unsigned.ipa` is an unsigned iPhoneOS `Payload/*.app` container intended for a signer to process with their own certificate and provisioning profile.

Neither artifact can be installed directly on a physical iPhone. The recommended route is still to build the source in Xcode with your own Team as described above; this lets Xcode create the correct entitlements and provisioning profile instead of attempting an error-prone manual re-sign.

## Connect to Windows

1. Start Codex LAN Bridge on the Windows computer.
2. Make sure the iPhone can reach the computer through the same trusted LAN or through Tailscale.
3. Enter the bridge address, such as `http://100.x.x.x:8787`.
4. Enter the six-digit pairing code shown by the Windows bridge.

The app accepts plain HTTP because LAN and Tailscale IP addresses usually do not have public TLS certificates. Plain HTTP should only be used on a trusted LAN or inside Tailscale. The pairing token is protected at rest by Keychain, but ordinary LAN HTTP is not encrypted on the wire.

## iOS limitations

- iOS does not allow a third-party app to keep an unrestricted, permanent polling service alive in the background. Background App Refresh is scheduled by iOS and may be delayed or skipped because of battery state, Low Power Mode, network conditions, or user settings. Reliable real-time notifications while the app is fully suspended require an APNs push service and Apple signing credentials, which are not included.
- Standard URL downloads are supported. `blob:` downloads created entirely inside JavaScript are left to WebKit and may not expose a shareable file on every iOS version.
- Both GitHub artifacts are unsigned. The simulator build is simulator-only, while the iPhoneOS IPA must be signed with the owner's certificate and provisioning profile before any physical iPhone can install it.
- If a remote program listens only on the Windows loopback interface, changing the displayed host is not enough. The Windows bridge still has to expose or relay that port.
