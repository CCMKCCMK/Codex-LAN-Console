# Privacy Notice

Effective date: July 22, 2026

This notice describes the current private-preview implementation of Codex LAN
Console. It covers the Windows bridge, web interface, Android client, and iOS
client supplied from this repository.

## Local-first design

The project does not currently operate a central Codex LAN Console cloud
service, advertising network, analytics service, or project-specific telemetry
collector. The bridge runs on the user’s Windows computer, and the mobile
client connects directly to that bridge.

The bridge communicates with the locally installed Codex app-server. Codex may
communicate with OpenAI or other configured services under the user’s separate
account, organization settings, agreements, and privacy terms. This notice does
not replace those third-party terms.

## Data the bridge accesses

To provide its features, the bridge may access:

- Codex task metadata, recent task messages, tool summaries, approval requests,
  questions, goals, skills, and configured tools;
- Codex session files under the configured Codex home directory, including
  rollout history used to detect task state and notifications;
- project names, working-directory paths, and selected files;
- a limited list of related local processes and their status;
- files the user uploads from a phone or explicitly registers from a task
  workspace; and
- local development HTTP services when the user requests a temporary relay for
  a localhost link.

Codex task history remains governed by the installed Codex application. The
Console reads that history but does not promise to delete the original Codex
session files.

## Windows bridge storage

The default bridge data root is:

`%LOCALAPPDATA%\CodexLanConsole`

| Data | Default location | Retention |
| --- | --- | --- |
| Paired-device token hashes | `devices.json` | Until the local data is deleted or the bridge is uninstalled. Raw bearer tokens are not stored in this file. |
| Current six-digit pairing code | `pairing.txt` | Replaced when the bridge starts and after a successful pairing. Treat it as sensitive while valid. |
| Auto-approval setting and counters | `approval-settings.json` | Until changed or local data is deleted. |
| Notification events | `notification-events.json` | At most 500 events and no more than 7 days. |
| Uploaded file copies and lease registry | `Uploads\` and `Uploads\leases.json` | Uploaded copies are scheduled for deletion after 30 days. The service checks for expired data periodically. |
| Registered workspace-file leases | Lease registry | Normally expire after 1 day. Expiration does not delete the original workspace file. An uploaded file remains leased with its managed copy for up to 30 days. |
| Localhost relay grants | Memory only | Normally expire after 10 minutes and disappear when the bridge stops. |

Registering an existing workspace file creates a temporary access lease but
does not copy, move, or delete the original file. Upload cleanup is best effort;
unexpected shutdowns, filesystem errors, restored backups, or manual copies may
retain data longer.

The project writes operational errors to the bridge process’s standard output
or error streams. It does not currently create a persistent log file itself,
but the launcher, operating system, terminal, debugger, or hosting environment
may capture that output.

## Browser and mobile storage

The web interface can receive a 30-day, HttpOnly, SameSite=Strict session
cookie. The cookie is marked Secure only when the bridge is reached through
HTTPS. Clearing site data or deleting the corresponding paired-device state
invalidates normal use of that session.

The Android client stores the bridge address, bearer token, notification state,
cursor, and a bounded set of up to 256 recently seen event identifiers in
private app storage. The background-notification credential record, including
the token, is encrypted with a key held by Android Keystore. The server address
also has a private, application-only preference copy that is not encrypted.
Android backup is disabled for the app. The data remains until the app clears
it, app data is cleared, or the app is uninstalled.

When the user selects a document, Android may grant the app persistent read
access to that provider URI. The authorization can remain until the provider or
user revokes it, app data is cleared, or the app is uninstalled. Camera capture
files use the private app cache and are normally cleaned after about one hour,
with a best-effort stale sweep after 24 hours. Files the user downloads are
placed in the device’s public Downloads collection and are not automatically
deleted by the app.

If the user has explicitly enabled background notifications, Android may
restart the notification service after device boot or an in-place app update.
The app requests platform permissions needed for notifications, foreground
service operation, network access, downloading on older Android versions, and
handing an APK to the system installer. The operating system remains
responsible for presenting and enforcing applicable permission controls.

The iOS client stores the bridge address and bearer token in the iOS Keychain
using a device-only accessibility class. Notification enablement, cursor,
server state, permission state, and WebView data may also be held in
UserDefaults, the app container, or the website-data store until cleared.
Keychain items may survive an app uninstall depending on platform behavior;
users who require complete removal should clear the saved connection before
uninstalling or review device Keychain state after removal. A file prepared for
the iOS share sheet is placed in the operating system’s temporary directory and
is deleted after the share operation completes where possible; abnormal
termination may leave cleanup to the operating system.

## Network transmission

The client sends task instructions, attachments, approvals, answers, and
control requests to the selected Windows bridge. The bridge returns task text,
paths, file content, process status, and notifications requested by the client.

Plain HTTP on a LAN does not encrypt this traffic. It is supported only on a
network the user fully trusts. For remote access, use a private encrypted
overlay such as Tailscale or Headscale. Never expose the bridge directly to the
public Internet or through router port forwarding.

Temporary localhost relays accept only authenticated requests from a granted
client address during the lease window, but they still extend a local
development service onto the selected private network interface. Open only
links you recognize and close the bridge when finished.

## Notifications

Notifications are intentionally generic on the lock screen where practical,
but the operating system may display task status and app identity. Notification
events can include task and turn identifiers internally. Device notification
settings, lock-screen settings, and system backups are controlled by Android,
iOS, or the device administrator.

## User choices and deletion

Users can reduce data processing by disabling background notifications,
avoiding file uploads, not enabling auto-approval, using lower permission
profiles, and stopping the bridge when it is not needed.

To remove bridge-managed data, stop the bridge and run the supplied uninstall
script, or delete `%LOCALAPPDATA%\CodexLanConsole` manually. This revokes all
paired devices and deletes managed uploads and settings. It does not delete the
project repository, original workspace files, Codex account data, or Codex
session history.

Clear or uninstall the mobile app separately. If a phone, token, or backup may
have been compromised, delete the Windows bridge data and pair trusted devices
again.

## Sale, advertising, and profiling

The current project does not sell personal data, serve advertising, or build a
cross-service advertising profile. If a future release adds a hosted service,
crash reporting, analytics, push provider, or other external processor, this
notice and the relevant app-store disclosures must be updated before that
feature is enabled.

## Children

The private preview is a developer tool and is not directed to children. It
should be used only by a person capable of understanding its system-access and
data-handling risks.

## Changes and questions

Material privacy changes must be documented in CHANGELOG.md and reflected in a
new effective date. For questions, use the private support route described in
SUPPORT.md. Do not include credentials or private task content in a support
request.
