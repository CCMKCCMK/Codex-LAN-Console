## Outcome

<!-- What user-visible outcome does this change produce, and why is it needed? -->

## Scope

- [ ] Web frontend
- [ ] Android client
- [ ] iOS client
- [ ] Windows bridge
- [ ] Bridge protocol or API
- [ ] Permissions or approvals
- [ ] Files, uploads, previews, or localhost relay
- [ ] Notifications or background behavior
- [ ] Build, release, signing, or dependencies
- [ ] Documentation only

## Verification

<!-- List exact automated checks, real-device tests, and manual flows completed. -->

- [ ] Relevant builds and automated tests pass.
- [ ] Every affected screen was inspected at supported phone widths.
- [ ] Long content, back navigation, safe areas, loading, empty, error, offline,
      and permission-denied states were checked where relevant.
- [ ] Existing paired-client upgrade behavior was tested or documented.

## Security and privacy

<!-- Explain new permissions, trust boundaries, data, listeners, and residual risks. -->

- [ ] The change does not expose the bridge directly to the public Internet.
- [ ] Least-privilege defaults and administrator/enterprise policies remain
      effective.
- [ ] No pairing code, bearer token, cookie, credential, personal file, private
      task, real hostname, IP address, username, or project path is included.
- [ ] New persisted data has a documented location, retention rule, and deletion
      path in PRIVACY.md.
- [ ] Authentication, approvals, full autonomy, file access, process control,
      relay, and untrusted content were reviewed where affected.
- [ ] SECURITY.md and docs/THREAT_MODEL.md were updated where needed.

## Licensing and supply chain

- [ ] I own this contribution or have authority to grant the rights described in
      CONTRIBUTING.md.
- [ ] Third-party or generated material is identified with its exact source,
      version, and license.
- [ ] THIRD_PARTY_NOTICES.md and the SBOM plan are updated where needed.
- [ ] No signing key, keystore, provisioning profile, token, password, or secret
      is committed or printed by CI.

## Compatibility and release notes

<!-- Describe migrations, breaking changes, rollback, and frontend/backend coordination. -->

- [ ] CHANGELOG.md is updated for user-visible behavior.
- [ ] docs/API.md and docs/ARCHITECTURE.md are updated where applicable.
- [ ] The frontend and backend versions are coordinated if the protocol changed.

## Screenshots or recordings

<!-- Use synthetic data and redact all identifying information. -->
