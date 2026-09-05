# Internal Bridge API

## Status

This is an internal, pre-release API used by the bundled web, Android, and iOS
frontends. It is not an OpenAI API, is not a supported public integration API,
and is not versioned independently from the application. Frontend and backend
must be upgraded together when protocol behavior changes.

The default base URL is `http://<private-host>:8787`. Never expose it directly
to the public Internet.

## Authentication

`GET /api/health` and `POST /api/pair` are unauthenticated. Every other `/api`
route requires either:

- `Authorization: Bearer <64-hex-character-token>`; or
- the HttpOnly `CodexLanSession` cookie.

Pairing request:

```json
{
  "code": "123456",
  "deviceName": "My phone"
}
```

Successful pairing returns the bearer token and machine name and also sets a
30-day, SameSite=Strict session cookie. The token is shown to the client only at
issuance; the bridge persists only its hash. `POST /api/session` exchanges an
already valid bearer token for the same session cookie.

Administrator Mode uses a separate protected device store. A successful pairing
closes its current enrollment window and later attempts return HTTP 403 with
`pairingClosed`. The local Windows manager can open a ten-minute, one-enrollment
window without revoking existing devices. Ordinary-mode tokens are not accepted.

Pairing failures are limited per client and globally. A six-digit code expires
after ten minutes and is invalidated immediately after successful use. Codes and
bearer tokens are secrets and must not appear in logs, URLs, issues, or screenshots.

## Common response behavior

JSON is used for normal structured requests and responses. Uploads use
`multipart/form-data`; file endpoints may stream a body with range support.

Errors may include:

```json
{
  "error": "Safe user-facing description",
  "kind": "invalidRequest",
  "code": null,
  "requestId": "opaque-request-id",
  "detail": "optional safe detail"
}
```

The `X-Request-ID` response header matches the diagnostic request identifier.
Clients must not treat internal exception text or an HTTP 500 response as safe
to display without redaction.

## Endpoint groups

### Service and session

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/health` | Service, pairing-open state, Codex readiness, machine, and time summary. No authentication; it does not reveal administrator state. |
| POST | `/api/pair` | Exchange the current six-digit code for a device token and cookie. No prior authentication. |
| POST | `/api/session` | Restore the browser cookie from an existing bearer token. |
| GET | `/api/summary` | Counts, latest bounded state, and authenticated Windows Bridge administrator-mode state for the overview screen. |
| GET | `/api/notifications/events` | Cursor-based notification feed with optional long polling up to 30 seconds. |
| GET | `/api/quota` | Cached Codex rate-limit window, remaining percentage, reset time, and three local burn-rate estimates for first-party widgets. |

### Tasks and permissions

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/threads?limit=` | List recent tasks from the Codex state database. |
| GET | `/api/threads/{id}?paged=true&cursor=&limit=` | Read metadata and a cursor-paginated page of recent turns. Full-history loading is rejected. |
| POST | `/api/threads` | Create a task with working directory and execution permissions. |
| POST | `/api/threads/{id}/messages` | Durably queue text, attachments, and skill references; returns a command receipt. |
| POST | `/api/threads/{id}/steer` | Durably queue input intended for an active turn; stale turn IDs are reconciled before dispatch. |
| GET | `/api/threads/{id}/commands?limit=` | Read recent durable command receipts for the task. |
| GET | `/api/threads/{id}/commands/{receiptId}` | Read one command receipt. |
| DELETE | `/api/threads/{id}/commands/{receiptId}` | Cancel a command that has not begun dispatch. |
| POST | `/api/threads/{id}/interrupt` | Interrupt the current bridge-controlled turn. |
| GET | `/api/models?forceRefresh=` | List the app-server model catalog and each model's supported reasoning efforts in advertised order. |
| GET | `/api/permissions?cwd=` | List effective permission profiles for a workspace. |
| GET | `/api/skills?cwd=&forceReload=` | List available skills. |
| GET | `/api/tools?threadId=` | List available MCP servers, apps, and tools. |

Task history pages are limited to 1–20 turns per request. A cached client that
does not request cursor pagination receives HTTP 426. An older Codex app-server
without safe turn pagination receives HTTP 501 rather than an unsafe
full-history fallback.

`POST /api/threads/{id}/messages` and `POST /api/threads/{id}/steer` accept
optional `model` and `reasoningEffort` strings. The Bridge resolves a catalog
preset id to its canonical model slug, requires the model to be present in the
current `model/list` response, and requires `reasoningEffort` to appear in that
model's advertised `supportedReasoningEfforts` list. An effort cannot be sent
without a model. Omitting both preserves the task's existing Codex settings.
The canonical options are stored in the durable command receipt and survive a
Bridge restart.

Codex supports `model` and `effort` overrides on `turn/start`, but not on
`turn/steer`. Therefore a queued command with either override waits for an
active turn to finish and then starts a fresh turn carrying those exact fields;
the Bridge never silently steers it into a turn running a different model.

### Approvals and questions

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/approvals` | List current approval requests visible to the bridge. |
| POST | `/api/approvals/{key}` | Accept, accept for session, decline, or cancel one request. |
| POST | `/api/approvals/approve-all` | Accept the current supported approval queue. |
| GET | `/api/approval-settings` | Read persistent auto-approval settings and counters. |
| POST | `/api/approval-settings` | Enable or disable persistent auto-approval. Enabling requires the exact confirmation phrase. |
| POST | `/api/pending/{key}/answers` | Answer a structured user-input request. |
| POST | `/api/pending/{key}/elicitation` | Accept, decline, or cancel an MCP form or URL elicitation with typed content and optional advertised persistence. |

Auto-approval does not invent answers for user questions or arbitrary MCP
elicitation forms. It may accept an MCP request only when that request explicitly
identifies itself as a tool approval and its required response can be constructed
from constants, defaults, approval booleans, or approval enums. Approval behavior
is security-sensitive and must remain consistent with the effective Codex and
organization policy.

### Files and local links

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/api/files/upload?threadId=` | Upload 1–10 attachments with a total request limit of 256 MiB and 128 MiB per file. |
| POST | `/api/files/register` | Create a lease for an existing file inside the task workspace. |
| GET | `/api/files?threadId=` | List active leases. |
| GET | `/api/files/{id}/download` | Authenticated attachment download. |
| GET | `/api/files/{id}/view/{**subpath}` | Authenticated, sandboxed preview and related relative assets. |
| DELETE | `/api/files/{id}` | Revoke a lease and delete its managed upload copy where applicable. |
| POST | `/api/local-links/resolve` | Create a short-lived authenticated private-network relay for an eligible localhost HTTP URL. |

Only files within the validated task workspace may be registered. Only
localhost, loopback, `0.0.0.0`, or `*.localhost` HTTP development URLs are
eligible for relay. Local HTTPS is rejected because its certificate normally
does not validate after remapping.

### Goals and commands

| Method | Path | Purpose |
| --- | --- | --- |
| GET/PUT/DELETE | `/api/threads/{id}/goal` | Read, update, or clear a task goal. |
| POST | `/api/threads/{id}/compact` | Request task compaction. |
| GET | `/api/commands` | List commands supported by the mobile command panel. |
| POST | `/api/threads/{id}/commands` | Execute a supported task command such as status, skills, tools, compact, or goal. |

### Local inventory

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/projects` | List recognized local projects for the project screen. |
| GET | `/api/processes` | List the allowlisted related processes. |
| POST | `/api/processes/{pid}/stop` | Stop an allowlisted process tree after exact `STOP <pid>` confirmation. |
| GET | `/api/diagnostics/console-launches?limit=` | Read the bounded, aggregated console-launch audit. This route is intentionally excluded from `/api/summary`. |
| POST | `/api/diagnostics/console-audit/capture` | Pause or resume capture with an `enabled` boolean. |
| POST | `/api/diagnostics/console-audit/clear` | Clear in-memory records after exact `CLEAR AUDIT` confirmation. |

Console audit records identify the source process, command process, window
host, parent chain, paths, first and last observation times, count, interval,
classification, and explanation. Command-line fields are redacted on the
server before transmission. The audit does not persist records and does not
itself invoke PowerShell or another terminal.

## Concurrency and ownership

A message accepted from the phone is persisted before app-server dispatch.
Both message endpoints return HTTP 202 with this shape:

```json
{
  "queued": true,
  "receipt": {
    "id": "opaque-receipt-id",
    "status": "queued",
    "message": "Safe user-facing status",
    "threadId": "task-id",
    "createdAt": "2026-07-27T02:00:00Z",
    "updatedAt": "2026-07-27T02:00:00Z"
  }
}
```

Receipt states are `queued`, `dispatching`, `delivered`,
`dispatchUncertain`, `failed`, and `cancelled`. Commands are ordered per task.
A fresh turn owned by Codex Desktop leaves the command queued until that turn
ends or its ownership evidence becomes stale. If the app-server disconnects
after a request may have been written but before acknowledgement, the receipt
becomes `dispatchUncertain`; the bridge reconciles it using the original
`clientUserMessageId` and persisted protocol history, and never blindly replays
it. Execution permission fields are persisted with the command unchanged.

## Compatibility and change control

Do not build a third-party client against this API during the private preview.
Any route, field, status, limit, or authentication detail may change. Endpoint
changes require coordinated frontend tests, CHANGELOG.md updates, and security,
privacy, and threat-model review where relevant.
