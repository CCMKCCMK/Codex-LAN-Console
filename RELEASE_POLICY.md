# Release Policy

Codex LAN Console is currently distributed only as a private preview from a
private repository. A GitHub release or version tag does not make the project
open source and does not override LICENSE or BETA_EULA.md.

## Release channels

- **Development:** untagged commits and local builds; may be incomplete and are
  not distributed to testers.
- **Private preview:** tagged builds supplied to explicitly authorized testers;
  proprietary and governed by BETA_EULA.md.
- **Public preview:** not yet authorized; requires every gate in
  docs/PUBLIC_RELEASE_CHECKLIST.md.
- **Stable:** not yet defined; requires a support window, migration policy,
  public license decision, branding clearance, and signed release process.

## Versioning

Releases use `MAJOR.MINOR.PATCH` labels for coordinated frontend/backend release
tracking. During the private preview:

- the HTTP API is unversioned and may change between releases;
- mobile and bridge builds that depend on the same protocol must be released
  together;
- a patch should contain compatible fixes where practical;
- a minor release may add features or require a coordinated upgrade; and
- a major release may make broad product or data changes.

This policy does not promise Semantic Versioning compatibility until a public
API stability policy is adopted.

## Release artifacts

A complete private-preview release may contain:

- a self-contained Windows x64 bridge bundle with the packaged web frontend;
- a signed Android APK using the project’s established private signing key;
- an unsigned iPhoneOS IPA for a tester to sign independently;
- an unsigned iOS simulator build;
- an iOS source archive for Xcode self-signing;
- `SHA256SUMS.txt` covering every downloadable artifact; and
- release notes identifying security, privacy, permission, compatibility, and
  migration changes.

An unsigned iOS artifact must always be labeled as unsigned. The project does
not collect, host, or manage a tester’s Apple credentials, certificate, private
key, provisioning profile, or Developer Team.

## Required verification

Before creating a tag:

1. Review the exact commit and confirm the working tree contains no unintended
   changes.
2. Build the Windows solution and run bridge protocol tests.
3. validate browser JavaScript and visually test all primary mobile pages.
4. Run Android lint and build checks.
5. Run iOS simulator tests and verify device and simulator artifacts on a macOS
   runner.
6. Test pairing, session restoration, notification delivery, approvals,
   questions, uploads, downloads, Markdown, local-link relay, back navigation,
   permissions, and task-state reporting.
7. Perform a secret and personal-data scan over the complete Git history and
   release archive.
8. Update CHANGELOG.md, versions, third-party notices, privacy disclosures, and
   checksums.
9. Confirm auto-approval is off by default and that public-Internet exposure is
   neither enabled nor recommended.

Failures block the release. Do not waive a security, signing, licensing, data
loss, or authentication failure merely to meet a date.

## Signing and secrets

Signing material and service tokens must never be committed, added to release
archives, printed in CI logs, embedded in source, or shared in an issue. Build
jobs receive only the minimum required secret at runtime. Rotate a secret
immediately if it is exposed.

Android release signing must preserve the certificate used by prior versions so
authorized testers can install an upgrade. iOS artifacts remain deliberately
unsigned until a documented signing strategy is adopted.

## Licensing and supply chain

Each release must preserve LICENSE, BETA_EULA.md where applicable, NOTICE.md,
THIRD_PARTY_NOTICES.md, and all required third-party license files. A
self-contained .NET bundle must include the exact Microsoft .NET license and
third-party notices for the runtime used.

Before public release, generate an SPDX SBOM, pin third-party GitHub Actions to
reviewed commit SHAs, add build-provenance attestations, enable dependency and
code scanning, and verify artifacts from a clean checkout.

## Tags and release replacement

Treat a published tag as immutable. If an artifact is wrong or unsafe, withdraw
the release, publish an advisory when appropriate, and create a new patch
version rather than silently replacing files under the same tag.

Private preview artifacts may be removed from access at any time. Testers should
retain only the newest approved build and delete withdrawn builds.

## Public-release authority

Only the copyright holder may approve a change from private preview to public
distribution or replace the proprietary LICENSE with an open-source license.
Making the repository public before that explicit decision is prohibited.
