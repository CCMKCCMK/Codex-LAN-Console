# Private Preview End-User License Agreement

Effective date: July 22, 2026

This Private Preview End-User License Agreement ("Agreement") applies to
pre-release builds of Codex LAN Console supplied directly by Wenchang Chai
("Licensor"). The software is an experimental remote-control interface for a
locally installed coding-agent environment.

By installing or using a private-preview build, you agree to this Agreement. If
you do not agree, do not install or use the software.

## 1. Limited evaluation license

Licensor grants an expressly authorized tester a personal, revocable,
non-exclusive, non-transferable, non-sublicensable license to install and use
the supplied object-code build solely for private, non-commercial evaluation on
devices and computers the tester owns or is authorized to control.

This license does not grant access to, or rights in, the private source-code
repository. It does not authorize public distribution, resale, hosting as a
service, publication of modified builds, or use in production or safety-critical
systems.

## 2. Restrictions

Except where applicable law does not permit a restriction, you must not:

- copy or redistribute the build to anyone who has not been expressly
  authorized by Licensor;
- remove copyright, license, security, or attribution notices;
- circumvent pairing, authentication, approval, sandbox, or device-security
  controls;
- expose the bridge directly to the public Internet or configure public router
  port forwarding to it;
- use the software to access any account, device, network, file, or service
  without the owner’s authorization;
- use the software to develop, deploy, or facilitate malware, credential theft,
  destructive activity, surveillance, or any unlawful conduct; or
- claim that the software is produced, endorsed, certified, or supported by
  OpenAI, the University of California San Diego, or the Halicioglu Data Science
  Institute.

## 3. High-privilege operation

The software can issue commands, read and modify files, stop processes, upload
and download files, approve tool calls, and interact with network services. In
"Full autonomous" mode it may run with the current Windows user’s permissions,
without a file sandbox and without individual approval prompts.

You are responsible for selecting appropriate permissions, reviewing the
environment, maintaining backups, protecting paired devices and tokens, and
stopping the bridge when it is not needed. A lost paired device or token may
provide control comparable to local access by the Windows user.

## 4. Network requirements

The bridge is intended only for a trusted local network or a private encrypted
overlay network such as Tailscale or Headscale. Plain HTTP on a local network
does not provide transport encryption by itself. Do not expose the bridge on a
public IP address, public Wi-Fi, or an untrusted network.

## 5. Data and third-party services

The software may read local coding-agent task history, project paths, process
information, and files selected by the user. Uploaded copies and operational
state may be retained locally as described in PRIVACY.md. The locally installed
coding agent may communicate with OpenAI or other configured third-party
services under the tester’s separate accounts and agreements.

Third-party components remain governed by their own licenses. See
THIRD_PARTY_NOTICES.md.

## 6. Confidential preview and feedback

Unless Licensor authorizes otherwise in writing, non-public builds, source code,
credentials, security findings, and unpublished product information are
confidential. Security findings must be reported privately under SECURITY.md.

You may provide feedback voluntarily. You grant Licensor a perpetual,
worldwide, royalty-free right to use, modify, and incorporate that feedback
without an obligation to compensate you. This clause does not transfer
ownership of code submitted as a contribution; contributions are handled under
CONTRIBUTING.md and any required separate contributor agreement.

## 7. Updates, support, and termination

The software may change, stop working, or be withdrawn at any time. No update,
support level, compatibility period, or public release is promised. Licensor may
terminate this license at any time. On termination, you must stop using and
delete all supplied builds and confidential materials in your possession.

## 8. Disclaimer

TO THE MAXIMUM EXTENT PERMITTED BY LAW, THE SOFTWARE IS PROVIDED "AS IS" AND
"AS AVAILABLE," WITHOUT WARRANTIES OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, TITLE, NON-INFRINGEMENT,
SECURITY, AVAILABILITY, OR ERROR-FREE OPERATION.

## 9. Limitation of liability

TO THE MAXIMUM EXTENT PERMITTED BY LAW, LICENSOR WILL NOT BE LIABLE FOR ANY
INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, CONSEQUENTIAL, OR PUNITIVE DAMAGES,
OR FOR LOSS OF DATA, PROFITS, BUSINESS, ACCESS, OR DEVICE AVAILABILITY, ARISING
FROM OR RELATED TO THE SOFTWARE OR THIS PRIVATE PREVIEW.

Some jurisdictions do not allow certain exclusions or limitations, so portions
of Sections 8 and 9 may not apply to you.

## 10. No affiliation

This is an independent, unofficial project. It is not affiliated with, endorsed
by, sponsored by, or supported by OpenAI, UC San Diego, or HDSI. "OpenAI,"
"ChatGPT," "Codex," "UC San Diego," and related marks belong to their
respective owners.
