# Support

Codex LAN Console is a private preview with no service-level agreement. Support
is provided on a best-effort basis only for the most recent build supplied by
the maintainer.

## Before requesting help

1. Confirm that the Windows bridge is running.
2. Confirm that the phone and computer are on the same trusted LAN or the same
   private Tailscale/Headscale network.
3. Upgrade the frontend and backend together to the latest supplied version.
4. Reproduce the issue with the lowest practical permission profile and with
   auto-approval disabled.
5. Check the current README, CHANGELOG.md, and known limitations in this file.

## Requesting support

Authorized private-preview users should use the private repository issue
tracker or the existing private collaboration channel. A support request should
include:

- app and bridge version;
- Windows, Android, or iOS version and device class;
- LAN, Tailscale, Headscale, or other private-network mode;
- the page and action that failed;
- sanitized steps to reproduce;
- the displayed request ID and exact safe error message; and
- tests already attempted.

Do not attach pairing codes, bearer tokens, session cookies, credentials,
private task text, personal files, public IP addresses, or unredacted logs and
screenshots. Replace usernames, hostnames, project paths, task IDs, and network
addresses with neutral placeholders.

Security vulnerabilities must follow SECURITY.md and must not be submitted as a
normal support issue.

## Supported use

Support currently covers:

- a Windows bridge run by the same user whose local Codex environment it uses;
- current Android and iOS private-preview clients;
- the bundled web frontend;
- trusted LAN operation; and
- private encrypted overlays such as Tailscale or Headscale.

## Unsupported use

The following are outside the supported configuration:

- direct public-Internet exposure or router port forwarding;
- shared, kiosk, multi-tenant, enterprise-server, or production deployment;
- use on systems the user is not authorized to control;
- bypassing Windows, Codex, organization, or enterprise policies;
- third-party forks, modified binaries, repackaged APKs, or unofficial signing;
- iOS provisioning, Apple-account administration, or third-party sideloading
  services;
- recovery of deleted files or task history;
- troubleshooting the OpenAI account, Codex service, Tailscale service, mobile
  operating system, or other third-party product itself; and
- safety-critical, regulated, medical, financial, industrial-control, or
  emergency use.

The maintainer may still offer guidance for an unsupported configuration, but
that does not make the configuration supported.

## Feature requests

Describe the user problem, not only a proposed implementation. State the
expected security, privacy, permission, storage, network, and compatibility
effects. There is no commitment to implement, publish, or maintain a requested
feature.
