# Contributing

Codex LAN Console is currently a private, proprietary preview. Contributions
are accepted only from people whom the repository owner has expressly invited.
Access to the repository and submission of a pull request do not grant a right
to use or redistribute the project.

## Before contributing

1. Confirm that the proposed work is in scope with the maintainer.
2. Read LICENSE, SECURITY.md, PRIVACY.md, and docs/THREAT_MODEL.md.
3. Do not begin work that requires real user credentials, private task history,
   personal documents, or public exposure of a bridge.
4. Report suspected vulnerabilities privately instead of opening a normal pull
   request or issue.

## Contribution rights

You must own the contribution or have written authority to submit it. You must
identify copied, generated, adapted, or third-party material and its license.
Do not submit code from an employer, university, research sponsor, confidential
repository, or restricted dataset unless you have confirmed that you may grant
the rights below.

Unless a separate written contributor agreement applies, by intentionally
submitting a contribution you retain your copyright and grant Wenchang Chai a
perpetual, irrevocable, worldwide, non-exclusive, royalty-free license to use,
reproduce, modify, prepare derivative works from, publicly display, publicly
perform, distribute, sublicense, relicense under proprietary or open-source
terms, and commercialize that contribution. You also grant a corresponding
patent license for patent claims you can license that are necessarily infringed
by your contribution alone or in combination with the project.

You represent that you have the authority to grant these rights. The maintainer
may require a separate signed contributor agreement before merging. A pull
request is not accepted until it is merged.

## Repository layout

- `frontend/web`: browser interface and shared web experience
- `frontend/android`: native Android wrapper and background notification client
- `frontend/ios`: native iOS wrapper and notification client
- `backend/bridge`: Windows bridge, authentication, API, file handling, relay,
  and Codex app-server integration
- `backend/bridge.tests`: protocol and policy test harness
- `docs`: architecture, API, security, release, and public-readiness documents

The web frontend remains a static application packaged into and served by the
Windows bridge. Do not add a second public web service without an approved
architecture and threat-model change.

## Development checks

Run the checks relevant to the change. From the repository root:

```powershell
dotnet build CodexLanConsole.sln -c Release
dotnet run --project backend/bridge.tests/CodexLanBridge.ProtocolTests.csproj -c Release --no-build
node --check frontend/web/app.js
```

For Android:

```powershell
Set-Location frontend/android
.\gradlew.bat lintDebug assembleDebug --no-daemon
```

iOS builds require macOS and Xcode. Follow `frontend/ios/README.md` and run the
simulator tests before requesting review. Never add signing certificates,
provisioning profiles, private keys, keystores, or passwords to the repository.

## Pull requests

Keep each pull request focused. Complete the pull-request template and include:

- the user-visible outcome and reason for the change;
- affected frontend, backend, protocol, data, and permission boundaries;
- tests run and their results;
- screenshots for every affected mobile or web screen;
- privacy, security, storage, and migration effects;
- third-party dependency and licensing changes; and
- compatibility impact for existing paired clients.

Changes to authentication, pairing, permissions, approvals, file access,
process control, local-port relay, uploads, notifications, signing, or release
workflows require explicit security review.

## Security and privacy rules

- Preserve least-privilege defaults. High-risk modes must be explicit and
  accurately represented.
- Never weaken administrator or enterprise policy enforcement.
- Do not expose the bridge directly to the public Internet.
- Do not log bearer tokens, pairing codes, session cookies, file contents, or
  unredacted task text.
- Use synthetic fixtures. Remove usernames, machine names, IP addresses, task
  identifiers, and project paths from tests and screenshots.
- Document every new persisted data item, location, retention rule, and deletion
  path in PRIVACY.md.
- Bound untrusted input, response sizes, collections, file counts, and history
  pagination.

## Dependencies

Prefer platform libraries and small, auditable dependencies. A new or upgraded
dependency must have an exact version, source, compatible license, security
review, and updated THIRD_PARTY_NOTICES.md and SBOM plan. Vendored files must
retain upstream notices. Dependencies with unclear ownership or restrictive
terms will not be accepted.

## Documentation and compatibility

Update CHANGELOG.md for user-visible behavior. Update docs/API.md for endpoint
or protocol changes, docs/ARCHITECTURE.md for component changes, and
docs/THREAT_MODEL.md for new trust boundaries. During the private preview the
API is not stable, but frontend and backend changes must be shipped together
when they depend on one another.
