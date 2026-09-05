# Changelog

This file records notable user-visible changes. The project is an open-source
preview; version numbers currently identify coordinated application releases
and do not promise a stable public API.

## Unreleased

## 1.9.0 - 2026-09-04

- Owner-authorized MIT public-source preview; removed personal location
  defaults and kept private runtime data outside source/distribution.
- Scooter full-charge cycles, ride recording, distance/time/elevation history,
  conservative terrain-aware range model and charger-return warning.
- Android user-started location foreground service, private offline queue,
  idempotent upload and timestamped stop recovery; native permissions explicit.
- Model confidence/sample count, history and data export; no claim of real BMS
  telemetry or validated multi-week prediction accuracy.
- Preserved unified navigation and notification intent handling.

## 1.8.2 - 2026-09-04

- Web style revision 53: Commute shares Console's dark/mint theme, system font,
  product header, persistent navigation and dark dialogs. Removed its separate
  brand/sidebar. Static-only rollout does not restart Bridge or running tasks.

- Fixed `/commute/` serving the Console fallback page with HTTP 200 instead of
  the commute application. Added explicit routing and content-aware live checks.
- Unified daily navigation: Tasks, Commute, Remote Control, Settings. Tasks is
  the default home; process diagnostics and approval management live in Settings.
- New task creation includes an optional project picker instead of a separate
  Projects tab or mandatory Windows-path prompt.
- Remote Control opens directly to its task form. Actual task questions remain
  visible in the conversation and as a pending-action link in Tasks.
- Added return links and notification deep links between commute and tasks;
  retained navigation in task details and stopped the root offline cache from
  silently replacing a commute page with Console HTML.

## 1.7.10 - 2026-09-04

- During first-turn initialization, Codex 0.153.1 briefly returns
  `paginated_threads is not supported yet` before the start acknowledgement.
  The mobile detail page now uses live state in this verified start window
  instead of incorrectly requiring a Codex upgrade. Other history errors remain
  visible. RPC rejections now have redacted diagnostics even outside the outbox.

## 1.7.9 - 2026-09-04

- Fixed the first message failing immediately when Codex reports that a new
  thread has not yet been materialized. Empty history no longer blocks turn/start.
- Empty threads retain their initial access until their first turn; completed
  tasks still release automatically. The old five-second release made empty IDs
  impossible to resume.
- Outbox failures now record the failing RPC method, request ID, command receipt,
  and redacted error. Mobile users can expand the concrete delivery error.
- Inherited client-owned dynamic tools receive a valid unsuccessful tool result
  when their executor is unavailable. This does not add Desktop tool executors to
  the standalone Bridge or claim an unavailable tool was executed.

## Earlier unreleased changes

### Added

- On-demand task access leases: read-only history browsing no longer resumes a
  task, interactive access is released shortly after completion, and a bounded
  idle sweep unsubscribes abandoned Bridge sessions.
- Private-preview proprietary license and limited beta EULA.
- Security, privacy, support, contribution, architecture, API, threat-model,
  release, and public-readiness documentation.
- Third-party attribution and an explicit public-release licensing checklist.
- Clear frontend/backend repository boundaries and coordinated CI coverage.

## 1.7.3 - 2026-07-27

### Added

- A user-chosen administrator code can pair additional trusted devices without
  reopening a local enrollment window. Only a slow salted verifier is stored;
  successful pairing still issues a separate random token for every device.

### Fixed

- Exceptions in live-event or notification observers can no longer disconnect
  the Codex app-server transport and interrupt a running turn.
- A Bridge-accepted orphaned turn is released only after the new app-server
  confirms that exact turn is no longer active.

## 1.7.2 - 2026-07-27

### Added

- Administrator Mode can enroll any number of trusted devices without revoking
  existing devices. Each local UAC-approved request opens one ten-minute window
  that closes immediately after one successful enrollment.
- The composer loads the live Codex model catalog and offers only the reasoning
  efforts advertised for the selected model.

### Security

- Pairing codes expire after ten minutes. Windows continues to persist only
  SHA-256 device-token hashes; the local enrollment request contains no code or
  raw token.
- Model and reasoning values are validated against the live app-server catalog
  before a command enters the durable queue.

### Fixed

- Model and reasoning selections survive Bridge restarts and are applied through
  the official `turn/start` fields. A running turn is never silently steered
  while claiming its model changed.

## 1.7.1 - 2026-07-27

### Fixed

- Phone instructions are durably queued before dispatch, safely survive Bridge
  reconnects, and use the app-server's canonical `userMessage.clientId` receipt.
- Preflight failures retry automatically, while requests that may already have
  crossed the app-server pipe are reconciled without duplicate execution.
- Oversized legacy history responses no longer terminate the command channel or
  prevent later phone instructions from reaching Codex.
- Live commentary, tool steps, and final answers remain visible in order without
  repeated full-history redraws.
- Touch scrolling now keeps its exact reading position. New content follows only
  when the reader is already at the bottom, with a separate new-message button
  otherwise.
- Public `.cer`, `.crt`, and `.der` certificate deliverables can be downloaded
  from the phone while private-key formats remain blocked.

## 1.7.0 - 2026-07-27

### Added

- An explicit Windows Administrator Mode that requires one local UAC consent,
  then lets only Bridge-owned and phone-started work inherit the elevated token.
- A single desktop CLI entry for enabling, disabling, and auditing the mode,
  while preserving ordinary Limited mode as the default.
- Authenticated mobile status that distinguishes Codex full autonomy from the
  actual elevation of the Windows Bridge process.

### Security

- Elevated releases are hash-checked during copying and stored under Program Files with protected
  ACLs before a Highest scheduled task may reference them.
- Administrator Mode uses a separate per-SID device store, accepts only its
  first paired phone, and listens only on loopback and active Tailscale IPv4
  addresses.
- A protected-executable firewall rule limits inbound access to TCP 8787 from
  the Tailscale IPv4 range; startup retries instead of staying loopback-only
  when Tailscale is not ready.
- The desktop manager can locally revoke the sole administrator phone and issue
  one replacement pairing code.
- UAC and the Windows secure desktop remain enabled; the application neither
  injects clicks into secure prompts nor exposes a remote elevation endpoint.

## 1.6.7 - 2026-07-27

### Fixed

- Every Desktop response item restores its turn directly from rollout metadata,
  so very large single turns no longer depend on a bounded history lookback.
- App-server reconnects retain independent Desktop progress, and batch pressure
  evicts old process rows before any assistant message.
- Mobile fallback history removes tool output, diffs, commands, arguments, and
  encrypted fields, including large bare Base64-like payloads.

## 1.6.6 - 2026-07-27

### Fixed

- Long-running Desktop tasks now stream their append-only rollout tail into the
  same bounded live-item store used by Bridge-owned tasks, without rescanning
  large history files.
- Mobile task details preserve the chronological sequence of commentary,
  reasoning, commands, file edits, browser checks, and failures as compact,
  individually expandable process rows.
- Turn context restores the active turn after a Bridge restart, and oversized
  tool output can no longer leave a completed step permanently marked running.

## 1.6.5 - 2026-07-27

### Fixed

- Desktop-owned tasks now expose a bounded, ordered tail of reasoning, command,
  file, browser, image, web, and sub-agent activity without sending raw output,
  diffs, arguments, or inline binary data to the phone.
- Process activity renders as compact individual rows inside a collapsible card,
  with filenames and progress states instead of aggregated protocol labels.
- Superseded recovery records are cleared as soon as a newer persisted turn is
  observed, so an old acknowledgement warning cannot block the composer.

## 1.6.4 - 2026-07-26

### Fixed

- Mobile task details now consume live item events, preserve every assistant
  message, and resume from a revisioned snapshot after a network interruption.
- Reasoning and tool activity is grouped into a compact, expandable
  `处理中`/`已处理` section instead of exposing raw protocol data.
- The always-on Windows task is never disabled by normal pause or repair
  operations, preventing an interrupted update from leaving the console offline.

## 1.6.3 - 2026-07-26

### Fixed

- Trusted Codex delivery files under the current task's dated
  `visualizations/<threadId>` directory can now be previewed and downloaded.
- Historical delivery files remain available when their original project
  directory has been removed or its remote drive is temporarily unmounted.
- Remote file cards now deduplicate concurrent registration, explain failures,
  preserve download actions when previews fail, and support an explicit retry.
- File leases reject cross-task access, sibling-file URL tampering, reparse
  points, and Windows alternate data streams.
- New tasks and message dispatch are owned by the Windows Bridge lifecycle
  after acceptance, so a phone or WebView disconnect cannot cancel preflight or
  clear a pending automatic recovery before Codex acknowledges the new turn.

## 1.6.2 - 2026-07-25

### Added

- A transparent, fixed 2x2 Android home-screen widget for live Codex quota,
  burn rate, reset time, and three independent remaining-time estimates.
- A matching lightweight transparent Windows desktop widget driven directly by
  the existing bridge process.
- Authenticated `GET /api/quota`, backed by the locally signed-in Codex
  app-server rather than web scraping or a second account login.

### Privacy and performance

- Quota forecasting stores only bounded percentage/timestamp samples under the
  current Windows profile; it does not store account identity or task content.
- Android reuses the existing encrypted pairing credentials and notification
  service. Without that service, the widget uses Android's 30-minute fallback
  plus manual refresh instead of starting another permanent background worker.

## 1.6.1 - 2026-07-24

### Added

- A bounded, in-memory Windows console-launch audit that observes brief CMD,
  PowerShell, and Windows Terminal appearances without launching another shell.
- A mobile “Processes and popup diagnostics” panel showing the originating
  process, command process, window host, parent chain, executable paths,
  frequency, occurrence count, and a redacted command summary.
- Pause, resume, clear, refresh, and explicitly confirmed source-process stop
  controls for the audit panel.

### Security and privacy

- Potential secret values in captured command lines are redacted by the bridge
  before any audit record is sent to a client.
- Audit records are kept only in bounded memory and disappear when the bridge
  stops; they are not added to overview polling or written to a log file.

## 1.6.0 - 2026-07-24

### Added

- Complete mobile handling for MCP form, OpenAI form, URL, and Computer Use
  tool-approval elicitations.
- Automatic host-time responses and explicit failure responses for unknown
  app-server request types so turns cannot remain pending forever.

### Changed

- New mobile installations start from the full-autonomous execution preset,
  with the existing one-time risk acknowledgement and lower-permission choices
  still available.
- Full-autonomous bridge-owned turns now auto-resolve residual command, file,
  permission, and MCP tool approvals without depending on the separate global
  auto-approval switch.

### Fixed

- Computer Use no longer reaches a phone-side dead end asking the user to
  return to the Windows desktop.
- MCP server requests are answered on the same app-server connection and with
  their protocol-specific response shape.
- One-time and session tool approvals can no longer inherit a longer-lived
  scope from a form default, constant, enum value, or manually submitted JSON.
- A newly created mobile task can be opened before its first message without
  surfacing the transient empty-rollout error.
- Interrupted or abandoned desktop turns no longer remain labelled as running
  for hours; persisted terminal turns now reconcile stale rollout evidence.
- Recent rollout file activity keeps genuinely long-running work live, while
  expired records become an explicit unknown state instead of a false running
  state or an input-blocking ownership conflict.
- A late summary response can no longer restore an old running badge after a
  newer task-detail refresh has cleared or completed that state.

## 1.5.0 - 2026-07-22

### Added

- Native iOS client and unsigned GitHub macOS build artifacts.
- Full autonomous permission preset, one-click approval, and persistent
  auto-approval controls with explicit warnings.
- Safe cursor pagination and a hard bound on Codex app-server response size.

### Changed

- Task history no longer falls back to loading a complete rollout into memory.
- Mobile clients share the same authenticated bridge API and web experience.
- Android signing configuration reads secrets from the build environment rather
  than source files.

### Fixed

- Fine-grained permission response handling.
- Large task histories causing multi-gigabyte bridge memory growth.
- iOS test host naming and invalid local-file URL normalization.

## 1.4.0

### Added

- Paginated task history and bounded mobile summaries.
- More accurate running, waiting, completed, stopped, and external-task states.
- Independent sandbox and approval-policy controls.

## 1.3.0

### Added

- Android background notifications for completed tasks and required actions.
- Encrypted Android storage for the bridge address and pairing token.
- Persistent notification cursor and duplicate-event protection.

## 1.2.1

### Fixed

- Native Android back gestures now close overlays and return through task and
  page history before exiting.

## 1.2.0

### Added

- Authenticated delivery-file viewing and downloading.
- Mobile attachment upload for documents, images, and video.
- Rendered Markdown, commands, skills, tools, goals, compact, and task-control
  actions.
- Remote localhost-link rewriting and temporary authenticated relays.
