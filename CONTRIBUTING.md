# Contributing

Contributions are welcome under the same MIT license as the project.
You retain copyright; submit only code and assets you have the right to
license. Identify third-party dependencies and preserve their notices.

Keep changes focused, describe user impact, and run relevant checks:

```powershell
dotnet run --project backend/bridge.tests/CodexLanBridge.ProtocolTests.csproj -c Release
node --test frontend/web/*.test.cjs
node --check frontend/web/commute/scooter.js
```

Android: run `frontend/android/gradlew.bat assembleDebug` from that directory.
iOS: follow frontend/ios/README.md on macOS.

Never commit credentials, private task content, user paths, pairing data,
location traces or signing keys. Use synthetic fixtures. Changes to
authentication, permissions, remote control or location collection must
document their security/privacy effects. Do not disable Windows security
controls or expose the live bridge to the public Internet.

Report vulnerabilities through GitHub private vulnerability reporting when
available; do not put exploit details or secrets in public issues.
