# Administrator Mode security boundary

Administrator Mode is a separate trust domain from the ordinary Bridge. It is
intended for a Bridge process that already owns an elevated Windows token; it
does not click, suppress, or weaken the Windows UAC secure desktop.

## Local activation and rollback

1. Open the single desktop `Codex LAN Console 开关.bat` manager.
2. Choose `7`, then choose Enable Administrator Mode and type `ADMIN`.
3. Approve the one Windows UAC prompt locally. The installer hash-checks the
   copied versioned repository release, places it under
   `%ProgramFiles%\Codex LAN Console\Bridge\<version>`, protects the tree, and
   only then registers the Highest scheduled task.
4. Pair the intended phone with the new administrator-only code shown by menu
   option `3`. Ordinary-mode tokens are intentionally not migrated.
5. Start a new task from the phone. Existing desktop-owned turns do not inherit
   the Bridge token.

To roll back, choose `7`, select Standard Mode, type `STANDARD`, and approve the
local UAC prompt required to replace the existing Highest task. Protected
administrator device tokens remain isolated and cannot authorize Standard Mode.
To add another trusted phone, choose `7`, then `3`, type `ADD`, and approve the
local UAC prompt. This preserves existing devices and opens one ten-minute
enrollment window; it closes immediately after one new device joins.

## Enforced boundaries

- Ordinary device registrations remain in the existing LocalAppData store and
  do not authorize Administrator Mode.
- Administrator registrations are stored per Windows SID below
  `ProgramData\CodexLanConsole\AdminCredentials`. The directory and credential
  files disable inherited ACL entries. SYSTEM and Administrators receive full
  control; the interactive account receives read/execute access so it can read
  the first-activation `pairing.txt` file.
- A fresh administrator store opens a ten-minute pairing window. A successful
  pairing closes that window, no replacement code is generated, and
  `pairing.txt` no longer contains a code. The elevated local manager can write
  a one-shot protected request that reopens another ten-minute, one-device
  window without deleting any existing device hash.
- Administrator Mode ignores configured URL/environment overrides. It listens
  only on IPv4 loopback and IPv4 addresses belonging to an active network
  adapter whose name or description identifies Tailscale. It never binds an
  ordinary LAN address or a wildcard address.
- Startup fails and lets the always-on task retry when no active Tailscale IPv4
  exists. It does not remain alive in a misleading loopback-only state.
- The installer maintains one fixed inbound firewall rule for the protected
  executable, TCP 8787, and remote IPv4 range `100.64.0.0/10`. Standard Mode
  removes that administrator-only rule.
- Elevation belongs only to work created by the elevated Bridge process. It
  does not elevate an already-running desktop Codex task or another program.

## Explicit residual trust

- During an enrollment window, a process already running as the same Windows
  user can read `pairing.txt`, because readable-by-current-user is a product
  requirement. Windows ACLs cannot distinguish a trusted interactive process
  from an untrusted process with the identical user token. The ten-minute
  expiry and immediate close after one enrollment bound this exposure.
- The private bootstrap scripts and repository release are user-writable and
  are not Authenticode-signed. Copy-time hashes prove only that the protected
  copy matches that local source, not who published it. Initial UAC consent
  therefore trusts the local installation as it exists at that moment. A public
  release requires a signed installer or a manifest anchored in a publisher
  key outside the user-writable tree.
- The elevated Bridge starts Codex, project commands, scripts, and tools with
  its inherited token. User-writable project content or developer tools can
  therefore execute as administrator when invoked. This is an intentional but
  high-risk consequence, not a least-privilege broker.
- Local Administrators and SYSTEM can replace or read the protected store by
  design. They are already above this boundary.
- The paired phone token is an administrator capability. Compromise of that
  phone, its token, the Bridge, or the tailnet can lead to administrator-level
  actions. Tailscale device identity and ACL policy remain external controls.
- Loopback HTTP is trusted to the local computer. Remote HTTP is accepted only
  on the Tailscale interface and relies on Tailscale for encryption and peer
  authentication.
- Administrator Mode does not expose or automate the Windows secure desktop.
  The one-time task installation/elevation confirmation remains a local Windows
  action.

There is intentionally no remote endpoint that reopens Administrator Mode
pairing or deletes its protected device registrations. Opening a new enrollment
window remains a local administrator operation.
