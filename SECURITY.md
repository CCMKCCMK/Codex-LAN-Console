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
sensitive rotating secret. It stays valid until the bridge restarts or a
pairing succeeds; it does not currently expire on a timer. The health response
may reveal the Windows machine name and service state to that trusted network.

## Full autonomous and auto-approval modes

Full autonomous mode can run with `danger-full-access` and `never` approval.
Persistent auto-approval can accept future supported approval requests. These
features intentionally reduce safeguards and may allow file deletion, process
execution, network access, credential access, or other destructive actions
under the current Windows user.

They must remain opt-in, visibly identified, and disabled unless the user
understands the consequences. They must not bypass an administrator or
enterprise policy. Security reports about an unexpected privilege escalation
or a misleading permission state are in scope; the documented consequences of
an explicitly selected high-privilege mode are not by themselves a defect.

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
