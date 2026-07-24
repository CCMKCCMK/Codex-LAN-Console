# Changelog

This file records notable user-visible changes. The project remains a private
preview; version numbers currently identify coordinated application releases
and do not promise a stable public API.

## Unreleased

### Added

- Private-preview proprietary license and limited beta EULA.
- Security, privacy, support, contribution, architecture, API, threat-model,
  release, and public-readiness documentation.
- Third-party attribution and an explicit public-release licensing checklist.
- Clear frontend/backend repository boundaries and coordinated CI coverage.

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
