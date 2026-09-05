# 1.9.0 — Open-source preview / Scooter

MIT open-source preview: remote tasks, Chrome entry, private commute tools and
experimental Scooter range tracking in one interface. Unofficial independent project.

## Install

- Windows: unpack the bridge ZIP, start CodexLanBridge.exe, and use the
  source repository's BAT/PowerShell manager to install optional startup.
- Android: update with Codex-LAN-Console-v1.9.0.apk. This non-debuggable
  preview is signed with the existing development certificate for update
  continuity. It is not a Play Store distribution.
- Current iOS source remains available, but Android background GPS is not
  implemented on iOS. No new iOS binary is claimed for 1.9.0.

Use a private network such as Tailscale. Do not forward the live control port
to the public Internet. Configure your own pairing code; none is embedded.

## Scooter

Full charge / start / stop / depleted recording, GPS distance, terrain-aware
equivalent range, charger return-route warning, offline Android buffering,
deduplicated uploads, history and private export. See docs/SCOOTER.md.
Precise location and notification permissions are user-controlled.

Battery percentage is a model estimate, not BMS telemetry. No real-world
weeks-long accuracy or complete Android OEM lock-screen validation is claimed.

## Verification

- Backend: 251 assertions passed.
- Navigation: 16 focused regressions passed.
- Android release build and vital lint passed; signing matches the prior APK.
- Current-source and reachable-history credential scan performed before publication.

Excluded: AppData, GPS logs, real locations, paired devices, access tokens,
keystores, private keys, user task history and development caches.
