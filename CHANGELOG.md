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
