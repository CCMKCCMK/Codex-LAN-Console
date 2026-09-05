# Security Policy

Codex LAN Console is a private-preview remote-control tool. A security defect
may expose source code, local files, credentials, coding-agent tasks, or the
full authority of the Windows user running the bridge. Treat security reports
as confidential.

## Supported versions

During the private preview, only the most recent release supplied by the
maintainer is supported. Older builds should be upgraded or removed. There is
no long-term-support branch and no compatibility guarantee for unpublished
builds.

## Reporting a vulnerability

Do not disclose a suspected vulnerability in a public issue, discussion, pull
request, screenshot, chat room, or social-media post.

While the repository is private, contact the repository owner through the
existing private collaboration channel and include the words "Security report"
in the subject or first line. After the repository becomes public, private
vulnerability reporting or a dedicated security address must be enabled before
public distribution; this file will then be updated with that exact channel.

Include, when available:

- affected version, commit, and platform;
- a concise impact statement;
- safe reproduction steps or a minimal proof of concept;
- whether the bridge was reached through LAN, Tailscale, Headscale, or another
  network;
- whether auto-approval or full autonomous mode was enabled; and
- a proposed mitigation.

Never send real pairing tokens, six-digit pairing codes, account credentials,
private task text, personal files, public IP addresses, or unredacted logs.
Use synthetic data and redact hostnames, usernames, project paths, and device
addresses.

The maintainer will make a reasonable effort to acknowledge a complete report,
assess severity, coordinate a fix, and credit the reporter if requested. These
are goals, not a service-level agreement or bug-bounty promise.

## Security-sensitive areas

Reports are especially useful when they concern:

- bypass of pairing, bearer-token, session-cookie, or file-lease checks;
- cross-device or cross-task file disclosure;
- path traversal, unsafe archive handling, or preview sandbox escape;
- remote command execution without a valid paired device;
- approval, permission, or enterprise-policy bypass;
- leakage of tokens through logs, URLs, redirects, cookies, notifications, or
  WebView/WKWebView storage;
- cross-site request forgery, cross-site scripting, unsafe Markdown, or hostile
  uploaded content;
- local-port relay access beyond the authorized client, port, or lease window;
- denial of service, unbounded task-history loading, or memory exhaustion;
- unsafe update, release-signing, or dependency-supply-chain behavior; and
- a way to expose the bridge publicly without an explicit warning.

## Deployment security requirements

The supported deployment is a trusted local network or a private encrypted
overlay network such as Tailscale or Headscale.

- Do not forward TCP 8787 from a router to the public Internet.
- Do not bind the bridge to a public cloud interface.
- Do not use plain LAN HTTP on public Wi-Fi or any network you do not fully
  trust.
- Stop the bridge when remote control is not needed.
- Protect every paired phone as if it could access the Windows account.
- Delete local bridge state and re-pair devices after a token or phone may have
  been compromised.
- Keep Codex, Windows, Android, iOS, WebView, and network-overlay software
  updated.

The unauthenticated health and pairing endpoints are reachable by devices that
can reach the bridge. Pairing is rate-limited, but the six-digit code remains a
sensitive rotating secret. Each code expires after ten minutes and is replaced
or closed immediately after a successful pairing. Administrator Mode opens a
new code only on first setup or after an explicit local Windows action. The
health response may reveal the Windows machine name and service state to that
trusted network.

## Full autonomous and auto-approval modes

Full autonomous mode can run with `danger-full-access` and `never` approval.
Persistent auto-approval can accept future supported approval requests. These
features intentionally reduce safeguards and may allow file deletion, process
execution, network access, credential access, or other destructive actions
under the current Windows user.

For a bridge-owned full-autonomous turn, residual app-server approvals are
treated as part of that same explicit choice. MCP tool approvals such as
Computer Use may persist an `always` grant only when the tool itself advertises
that option. Ordinary questions and business forms are never guessed or
auto-filled.

They must remain opt-in, visibly identified, and disabled unless the user
understands the consequences. They must not bypass an administrator or
enterprise policy. Security reports about an unexpected privilege escalation
or a misleading permission state are in scope; the documented consequences of
an explicitly selected high-privilege mode are not by themselves a defect.

## Windows Administrator Mode

Administrator Mode is a separate, explicit trust boundary. It requires local
Windows UAC consent to install or change the Highest scheduled task. The task
may execute only a hash-checked release copied beneath Program Files with protected
ACLs; it must never point at a user-writable repository or download directory.

Standard device registrations do not authorize Administrator Mode. The elevated
Bridge uses a separate per-SID protected credential store and binds only
loopback plus an active Tailscale IPv4 address. Additional Administrator devices
can be enrolled only through a ten-minute window opened locally with UAC; the
window closes after one successful enrollment and existing device hashes remain
valid. Every paired phone must be protected as an administrator credential.

The mode does not disable UAC or automate the Windows secure desktop. It affects
only child work created by that elevated Bridge process; existing desktop Codex
rounds and unrelated applications are outside its scope. See
`docs/ADMINISTRATOR_MODE_SECURITY.md` for the enforced and residual boundaries.

The private bootstrap is not Authenticode-signed. Its copy-time hashes detect
transfer changes but are not a publisher-authenticity check, so initial consent
trusts the local user-writable installation. The elevated Bridge also gives its
token to Codex and invoked project tools; running user-writable scripts in this
mode intentionally runs them as administrator. Public distribution is blocked
until the bootstrap is anchored in a publisher signature.

## Coordinated disclosure

Please allow a reasonable period for investigation and remediation before
publishing details. The maintainer may request additional validation, assign a
CVE where appropriate, prepare patched releases, and publish a security
advisory. Do not access data belonging to others, persist on a device, disrupt
service, or broaden testing beyond the minimum needed to demonstrate impact.

## Release security

Public releases must satisfy `docs/PUBLIC_RELEASE_CHECKLIST.md`, including
secret scanning, dependency review, signed or attestable artifacts, checksums,
third-party notices, and a private vulnerability-reporting channel.
