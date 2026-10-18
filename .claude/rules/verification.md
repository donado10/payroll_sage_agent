# What actually proves a change good

`scripts/check.sh`:

```
1/4 platform/ snapshot   SHA256SUMS holds, and no file was added — platform/ is what the handoff wrote
2/4 format               dotnet format --verify-no-changes
3/4 build                warnings are errors (Directory.Build.props)
4/4 test                 dotnet test — xUnit v3
```

Every `dotnet` command goes through `scripts/dotnet.sh`: **no .NET SDK is installed on this
machine**, the SDK runs in `mcr.microsoft.com/dotnet/sdk:10.0` with the repo mounted. Do not install
one, and do not run a bare `dotnet`.

## [HARD] What cannot be proven here

This machine is Linux. **Nothing Windows-only can be run on it**: the service's registration, its
start/stop under the Service Control Manager, the installer, DPAPI, the event log, a signed exe. Code
for those can be written and compiled, never executed here. When a change depends on one of them, say
so explicitly — « écrit, compilé, non exécuté : à tester sur Windows » — and never report it as
verified.

What CAN be run here: everything in `Core`, the service host in Docker (`UseWindowsService` is a no-op
off Windows), and SQL access against the SQL Server 2019 container of `test/sage/`.

## End to end, on this machine

The agent in Docker, against the restored Sage databases and a platform dev server on the CLI stack —
`docs/project-book/02-development/dev-environment.md` › « End to end ». Done 2026-10-17: two bindings
on one instance, 1 348 accounts each, a second sync unchanged, a platform revocation stopping its loop,
`remove-binding` unpairing while the service ran, and no token, password or account in the log. Phase 6
(same day) added the platform's screen and every failure case — `platform/plan.md` › Phase 6. Run it
again after changing the loop, the reader, the verbs or the client — the unit tests use fakes for both
ends and cannot see a real SQL Server or a real platform.

## The platform side

A change that needs the platform running (pairing, a job) needs igf_payroll's local stack — that
repo's `local-stack.md`. Ask the user before starting it.

## The Windows package

`scripts/package.sh` cross-publishes the exe for win-x64 (self-contained, single file) and zips it with
`installer/windows/`. What proves it HERE, and all that does:

- the build and the tests (`scripts/check.sh` — 113 on 2026-10-18);
- `file dist/…/app/PayrollSageAgent.exe` says PE32+ x86-64;
- both scripts PARSE, and `install.ps1`'s `Get-ServiceSid` gives TrustedInstaller's published SID —
  in `mcr.microsoft.com/powershell:7.4-ubuntu-22.04`, parsing with
  `[System.Management.Automation.Language.Parser]::ParseFile` (the command is in
  `docs/project-book/03-windows/installation.md`). PowerShell 7 parsing proves syntax, not
  Windows PowerShell 5.1 behaviour.

- **the MSI's own assertions** (`installers.py` › `assert_msi`, every build): the service under its
  virtual account, its control events, the three added tables, the purge and its condition (`KEEPDATA`
  included), the old version removed inside the new one's transaction, the upgrade code, the file count;
- **signing ON**, once, with a throwaway self-signed certificate (`SIGNING_ENABLED=1 SIGNING_DIR=…`):
  `osslsigncode verify` says ok for the exe and the MSI (2026-10-17).

The `pending\` requests (one binding and several in one file), `--config` (with several, the passwords
one stdin line each), the Windows-auth hand-off and `purge` were run end to end in Docker
against a platform on the CLI stack (2026-10-17) — Windows authentication itself only as far as Linux
goes: an SSPI refusal, the container has no Kerberos.

**On Windows, only what the user ran**: the zip (0.2.x) and then the MSI (0.4.1) installed on their Windows
Server 2025, paired over the WAN, online, synced (Phases 7–8). Reboot, revoke and uninstall were not run.

**The self-update (Phase 9)**: proven here up to the install — in Docker against igf_payroll's CLI stack,
the check, the streamed 39 MB download verified byte for byte, the minimum refusing the service and the
updater going out of its window (`03-windows/installation.md` › « Updates »); the decision, the install,
the health wait and the rollback through a fake `IUpdateMachine`. **`WindowsUpdateMachine` and
`UpdateTask` have never run** — msiexec, the scheduled task, the runner copy, WinVerifyTrust: report them
as not verified until the user's server installs 0.5.0 and then updates to a 0.5.1.
