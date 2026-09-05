# Notification navigation repair — 2026-09-04

## Root cause found in code

The backend serves the commute document; it does not instruct it to switch to a task.
Android retained `pendingNotificationThreadId` until an injected script acknowledged
both navigation and asynchronous task refresh. Page commits and activity resumes
replayed unacknowledged IDs indefinitely. A manual tab change did not cancel them.
The standalone commute page's legacy `openThread(id)` redirected real task IDs to
the Console task page, so a stale native request could undo the user's tab choice.
No phone-side navigation trace was available to identify a particular reported tap.

## Web fix (hot-deployed, no APK reinstall needed)

- Real task UI navigation is `openTask`; the native legacy `openThread` hook is separate.
- Legacy ID-only requests cannot prove whether the notification is fresh or replayed.
  They show a single non-blocking “查看任务” action instead of changing the current page.
- Both pages use the shared notification router. Explicit user navigation wins over
  older timestamped native requests, including across same-origin page loads.
- Updated native requests acknowledge route acceptance immediately; data loading
  continues separately and cannot trigger navigation retries.

## Android source fix

- Manual navigation clears the pending request (including hardware Back and SPA tabs).
- Retained requests expire after 60 seconds, with at most six brief readiness attempts.
- State restoration preserves expiry; old saved state without a timestamp is discarded.
- The modern delivery hook does not wait for a second network refresh.

The Android changes require a newly built and installed APK. Until then, the deployed
web legacy hook prevents automatic page jumping, with an extra “查看任务” tap when needed.

Pairing still uses the existing salted persistent administrator-code verifier.
No fixed code is embedded in JavaScript, HTML, or this repository.

## Focused verification

Run `node --test frontend/web/notification-navigation.test.cjs frontend/web/navigation.test.cjs`.
The cases cover repeated legacy delivery, explicit task opening, manual-navigation
precedence across page loads, new notifications, expiry, and frontend-readiness retry.
Run `scripts/Test-Navigation.ps1` against the local and Tailscale URLs after deployment.
