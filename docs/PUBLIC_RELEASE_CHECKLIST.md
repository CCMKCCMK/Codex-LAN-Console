# Public Release Checklist

The repository must remain private until the copyright holder explicitly
approves public release and every blocking item below is complete. Making a
GitHub repository public is not the same as licensing it as open source.

## 1. Ownership and licensing

- [ ] Confirm the legal copyright holder for all original code and assets.
- [ ] Confirm that no employer, university, research sponsor, collaborator, or
  funding agreement owns or restricts any part of the project.
- [ ] Review every commit for copied, generated, confidential, or
  third-party-controlled material.
- [ ] Choose the public licensing strategy in writing.
- [ ] If broad permissive open source is intended, obtain legal review of
  Apache-2.0 as the default candidate; do not choose MIT merely because it is
  shorter.
- [ ] If dual licensing or commercial relicensing is intended, adopt an
  appropriate contributor agreement before accepting outside contributions.
- [ ] Replace the private LICENSE and beta EULA only through an explicit owner
  decision; preserve the private license for historical private builds.
- [ ] Complete THIRD_PARTY_NOTICES.md and ship the exact license and NOTICE files
  for Marked, Gradle, the self-contained .NET runtime, and every added
  dependency.
- [ ] Generate and review an SPDX SBOM for every binary artifact.

## 2. Name, trademarks, and institutional references

- [ ] Complete a trademark review of the working name "Codex LAN Console."
- [ ] Prefer an independent product name and describe Codex compatibility in a
  subtitle rather than using another party’s mark as the app name.
- [ ] Remove OpenAI, ChatGPT, Codex, UC San Diego, and HDSI logos or confusing
  visual identity unless written permission exists.
- [ ] Keep the independent-project and non-endorsement notice in the repository,
  app About screen, website, and release descriptions.
- [ ] Confirm that the author’s GitHub biography states personal affiliation
  accurately without implying that UCSD or HDSI publishes or endorses the app.

## 3. Security readiness

- [ ] Obtain an independent security review of pairing, token storage, cookies,
  WebView/WKWebView, Markdown, previews, uploads, workspace validation, approval
  logic, full autonomy, process control, and localhost relay.
- [ ] Enable GitHub private vulnerability reporting and publish a monitored
  security contact.
- [ ] Define severity, supported versions, patch targets, and coordinated
  disclosure procedures.
- [ ] Verify that auto-approval is off by default and full autonomous mode is
  explicit, accurate, and covered by tests.
- [ ] Add individual paired-device listing and revocation, or document and
  formally accept the residual risk.
- [ ] Minimize unauthenticated health information or formally accept disclosure
  of machine name and service state on the trusted network.
- [ ] Review the default all-interface listener and provide a safer documented
  binding/firewall path.
- [ ] Test rate limiting, CSRF, XSS, path traversal, hostile files, relay SSRF,
  stale leases, token leakage, request smuggling, and denial of service.
- [ ] Repeat the large-history memory regression with representative real data.
- [ ] Verify enterprise and administrator policies cannot be bypassed.

## 4. Privacy and legal disclosures

- [ ] Validate the data inventory, paths, retention periods, deletion behavior,
  and mobile credential lifecycle in PRIVACY.md against a release build.
- [ ] Provide an in-app privacy link and effective date.
- [ ] Document whether future analytics, crash reporting, APNs, FCM, update
  checks, or hosted services process data externally.
- [ ] Complete applicable Android Data Safety and Apple privacy disclosures
  before store distribution.
- [ ] Review beta/stable terms, warranty disclaimer, limitation of liability,
  acceptable use, export, consumer, and jurisdiction requirements with
  qualified counsel.
- [ ] Confirm that uninstall and revocation instructions actually remove every
  project-managed token and copy, including platform credential stores.

## 5. Product and architecture

- [ ] Freeze and document the public frontend/backend boundary.
- [ ] Decide whether the bridge API remains internal or receives a versioned
  public compatibility policy.
- [ ] Define supported Windows, Android, iOS, Codex, and network-overlay
  versions.
- [ ] Define migration behavior for stored settings, tokens, uploads, and task
  protocol changes.
- [ ] Complete accessibility, localization, small-screen, rotation, safe-area,
  back-navigation, long-content, and offline-state testing on real devices.
- [ ] Clearly disclose iOS unsigned/self-signing limitations and background
  notification limits.
- [ ] Confirm no feature requires public port forwarding.

## 6. Build and supply chain

- [ ] Pin every third-party GitHub Action to a reviewed full commit SHA.
- [ ] Enable dependency graph, Dependabot, secret scanning, push protection,
  CodeQL or equivalent analysis, and branch/tag rulesets.
- [ ] Protect `main`, workflow files, CODEOWNERS, LICENSE, and release tags.
- [ ] Build every release from a clean, reviewed tag in CI.
- [ ] Generate checksums, signed build-provenance attestations, and SBOM
  attestations.
- [ ] Verify the Android signing certificate continuity and store the key in a
  documented recovery process outside the repository.
- [ ] Decide and document the supported iOS signing/distribution channel.
- [ ] Test that source and binary archives contain no token, password, key,
  keystore, provisioning profile, personal path, private IP, task history, or
  unintended debug artifact.
- [ ] Establish reproducible or independently verifiable build instructions.

## 7. Community and operations

- [ ] Finalize CONTRIBUTING.md, a contributor agreement or DCO decision,
  CODE_OF_CONDUCT.md, GOVERNANCE.md, SUPPORT.md, issue forms, and PR templates.
- [ ] Publish a roadmap and clearly label experimental versus stable features.
- [ ] Define maintenance ownership, backup maintainers, release authority, and
  account-recovery procedures.
- [ ] Publish support expectations and an end-of-life policy.
- [ ] Test the full installation, upgrade, rollback, revocation, and uninstall
  journey from the public artifacts.

## 8. Final go/no-go

- [ ] A release candidate has passed functional, security, privacy, licensing,
  and real-device acceptance review.
- [ ] All release artifacts, checksums, SBOMs, attestations, notices, and release
  notes match the final tag.
- [ ] The copyright holder has recorded the license, branding, and public-release
  decision.
- [ ] Repository visibility is changed only after the above artifacts and
  security-reporting channel are ready.

Any unchecked ownership, license, trademark, credential, authentication,
remote-execution, data-loss, or public-network item is a no-go.
