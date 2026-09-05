# Release policy

Version 1.9.0 starts the owner-authorized MIT open-source preview. It is not a
stable or independently security-audited release. Source visibility does not
expose a user's live service. Use loopback or a private encrypted network.

- Release source, changelog, checksums and build instructions together.
- Test affected protocol, persistence, authentication and notification paths.
- Scan current files, reachable Git history and exact distributable contents
  for credentials and personal data before publication.
- Never package AppData, GPS traces, user configuration, signing keys,
  session history or development caches.
- Keep signing certificate continuity for Android updates. The personal
  preview Android channel uses the existing development certificate, with
  debugging disabled in the release build. It is not a Play Store release or
  an independently verified publisher identity. Never distribute that key.
- iOS supports the common Web UI, but 1.9.0 Android background GPS is not
  implemented on iOS. Do not label the clients feature-identical.
- Mark experimental models and untested real-device behavior explicitly.
- Preserve upstream licenses and attribution in source and binary releases.
- Do not silently replace an already public version; publish a patch release.

Full device-matrix testing, independent security review, SBOM attestations,
reproducible CI artifacts and stable support commitments remain future work;
see docs/PUBLIC_RELEASE_CHECKLIST.md. No claim of completing them is implied.
