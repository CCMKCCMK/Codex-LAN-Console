# Threat Model

## Scope and security objective

This threat model covers the Windows bridge, bundled web frontend, Android and
iOS clients, pairing credentials, file leases, notifications, and temporary
localhost relays.

The primary objective is to let one authorized user control that user’s local
Codex environment from a trusted phone without turning the Windows account into
a public remote-execution service.

## Protected assets

- Windows user authority and local credentials
- source code, research data, documents, and workspace files
- Codex task history, prompts, outputs, goals, approvals, and tool configuration
- bearer tokens, session cookies, pairing codes, signing keys, and mobile
  credential stores
- integrity of permission, approval, and enterprise-policy decisions
- availability of the bridge, Codex app-server, phone, and computer
- release artifacts and repository history
- user privacy, device identifiers, paths, hostnames, and network addresses

## Trust boundaries

1. **Phone to private network:** the phone and computer may share a trusted LAN
   or a private encrypted overlay. Ordinary LAN HTTP is not encrypted.
2. **Network to bridge:** health and pairing are unauthenticated; other API
   routes require a paired-device token or cookie.
3. **Bridge to Codex app-server:** local JSON-RPC over standard input/output can
   initiate actions under the Windows user and configured Codex account.
4. **Bridge to filesystem and processes:** uploads, downloads, previews,
   projects, session files, and selected processes cross the operating-system
   boundary.
5. **Bridge to localhost service:** a relay temporarily extends a loopback-only
   HTTP service onto one private interface and granted client address.
6. **Administrator Bridge to Windows:** an explicitly elevated Bridge and its
   child tasks cross the UAC boundary; the paired phone becomes an administrator
   capability.
7. **Project to third-party services:** Codex, OpenAI, Tailscale, Headscale,
   GitHub, Apple, Google, Microsoft, and platform services have separate trust
   and policy boundaries.

## Assumptions

- The Windows user, computer, and phone are not already fully compromised.
- The user is authorized to access every controlled account, file, device, and
  network.
- The bridge is not exposed directly to the public Internet.
- The user protects the pairing code and paired phone.
- Tailscale/Headscale administration and device membership are configured
  correctly when used.
- Codex and organization policies are authoritative and not intentionally
  bypassed.

If an assumption is false, the system may not provide a meaningful security
boundary.

## Threats, controls, and residual risk

### Public or hostile network exposure

**Threat:** Internet scanning, pairing attacks, traffic interception, token
theft, denial of service, and remote command execution.

**Controls:** documented private-network-only deployment, bearer authentication,
pairing rate limits, bounded inputs, and recommendation of encrypted overlays.

**Residual risk:** the bridge listens on all interfaces by default and LAN HTTP
is plaintext. The health endpoint reveals machine and service state. A user can
still misconfigure a firewall or router. Public exposure is unsupported and
must remain a release-blocking warning.

### Pairing and lost-device compromise

**Threat:** guessing a six-digit code, stealing a raw bearer token, or using a
lost unlocked phone.

**Controls:** random ten-minute code, per-client and global failure limits,
256-bit device token, token hashing on Windows, Android Keystore encryption,
iOS Keychain, HttpOnly SameSite cookie, and immediate code invalidation after
pairing.

**Residual risk:** the code exists in plaintext locally, a token remains valid
until bridge data is removed, and there is no individual device-revocation UI.
A paired device should be treated as equivalent to remote Windows access.

### High-privilege task execution

**Threat:** a prompt, compromised phone, malicious file, tool, or mistaken user
causes destructive commands or data disclosure.

**Controls:** permission profiles, separate approval policies, explicit warning
for full access, confirmation for persistent auto-approval, and enterprise
policy precedence.

**Residual risk:** full autonomous mode intentionally removes important
safeguards. Approval is not a malware detector. Backups, least privilege, and a
stopped bridge remain necessary.

### Windows Administrator Mode

**Threat:** a user-writable elevated executable, reused ordinary token, exposed
LAN listener, compromised paired phone, or misleading UI silently converts
ordinary remote control into administrator execution.

**Controls:** one local UAC consent; hash-checked versioned release copied under
Program Files; protected non-inherited ACLs; Highest task path validation;
separate per-SID protected device store; UAC-protected local opening of a
ten-minute, one-enrollment window; authenticated status; and
loopback/Tailscale-only binding. Existing device hashes survive later enrollment.
The secure desktop is neither disabled nor remotely automated.

**Residual risk:** during first activation, another process with the same user
token can read the short-lived pairing code or tamper with the unsigned,
user-writable bootstrap before consent. Copy hashes are not publisher
authentication. Codex and invoked user-writable project tools inherit the
administrator token by design. A compromised paired phone, tailnet, local
administrator, SYSTEM process, or elevated Bridge has full administrator impact.
SmartScreen, credential, Windows Hello, and driver prompts can still require
local interaction.

### Concurrent task ownership

**Threat:** the bridge and desktop app-server mutate the same task, producing
conflicting approvals, turns, or state.

**Controls:** external-task observation is notification-only and mutating API
operations reject a known external-active conflict.

**Residual risk:** process crashes and incomplete upstream state can delay or
misclassify a transition. The user may need to return to the owning desktop
process.

### File upload, download, and preview

**Threat:** path traversal, oversized input, hostile HTML, script execution,
cross-task disclosure, stale file leases, or deletion of an original file.

**Controls:** safe generated physical names, canonical workspace checks,
authenticated opaque leases, file-count and size limits, range streaming,
preview Content Security Policy and sandboxing, relative-path validation,
expiry, and managed-upload deletion rules.

**Residual risk:** an authorized user can still download sensitive files. A
browser or media parser may contain vulnerabilities. Uploaded copies may remain
until cleanup, backup expiry, or manual deletion.

### Markdown and task content

**Threat:** untrusted task text injects HTML, scripts, deceptive links, huge
binary strings, or unreadable tool payloads.

**Controls:** bounded and paginated mobile summaries, escaped/rendered content,
file cards, preview isolation, and omission of large binary/tool bodies.

**Residual risk:** links can lead to hostile external sites, and a future
Markdown-library update can change sanitization behavior. Rendering changes
require security regression tests.

### Localhost relay

**Threat:** server-side request forgery, access to unrelated local services,
credential forwarding, unauthorized client access, or persistent exposure.

**Controls:** only recognized loopback HTTP URLs, rejection of HTTPS remapping,
exclusion of the bridge port, connectivity check, interface-specific listener,
client-address grant, bridge authentication, header rewriting, and a short
lease.

**Residual risk:** the chosen local service may have no authentication and may
trust localhost. Extending it even temporarily changes its threat model. Do not
relay unknown services.

### Resource exhaustion

**Threat:** multi-gigabyte task history, oversized JSON-RPC lines, upload floods,
large collections, or frequent polling exhaust memory, CPU, disk, or network.

**Controls:** cursor pagination, no full-history fallback, a bounded app-server
message reader, bounded mobile collections, upload limits, notification caps,
poll intervals, and lease cleanup.

**Residual risk:** a local Codex process or authorized client can still create
heavy work. Limits require regression tests against real large histories.

### Notification privacy

**Threat:** task information appears on a lock screen, duplicate notifications
cause confusion, or stale approvals remain actionable.

**Controls:** generic notification text where practical, cursor and duplicate
tracking, bounded seven-day server history, and live checks for pending actions.

**Residual risk:** operating-system notification settings and backups are
outside the project’s control. Users should configure lock-screen privacy.

### Supply-chain and release compromise

**Threat:** malicious dependency, mutable CI action, leaked signing key,
tampered APK/IPA/ZIP, missing third-party notice, or compromised maintainer
account.

**Controls:** private repository, restricted workflow permissions, ignored
signing files, checksums, CI validation, and documented release gates.

**Residual risk:** current actions are referenced by major tags and public
provenance/SBOM controls are not yet complete. Public release is blocked until
the checklist is satisfied.

## Out of scope

- A computer, phone, operating system, Codex installation, or maintainer account
  already fully compromised before use
- Malicious activity by the authorized Windows user
- Security or availability guarantees of OpenAI, Codex, GitHub, Apple,
  Tailscale, Headscale, or the local development service
- Public hosting, multi-tenancy, anonymous access, or use on an untrusted LAN
- Recovery of data deleted by Codex, the operating system, or the user

## Review triggers

Update this model before adding a hosted backend, APNs or FCM server, analytics,
crash reporting, public sharing, account login, individual cloud identities,
automatic updates, new network listeners, new persisted data, broader process
control, or a new full-autonomy behavior.
