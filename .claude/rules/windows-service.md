---
paths:
  - "src/PayrollSageAgent.Service/**"
  - "installer/**"
  - "scripts/package.sh"
  - "scripts/sign.sh"
  - "scripts/lib/installers.py"
  - "scripts/packaging.Dockerfile"
---

# The Windows service host

- The host is `Host.CreateApplicationBuilder` + `AddWindowsService`, which is a **no-op off
  Windows**. That is what lets the same exe run in Docker here, and it must stay true: nothing in
  `Program.cs` may require Windows to START.
- **[HARD] Anything Windows-only is written, compiled, and NOT run here** — service registration,
  recovery options, the event log, DPAPI, the installer. Say so when reporting; see `verification.md`.
- Windows-only APIs sit behind an interface declared in `Core`, with a Linux stand-in for
  development that is clearly NOT secure (and named so), never used on Windows.
- One loop per binding, started and stopped independently (`AgentSupervisor` re-reads the bindings
  folder every 10 s — no watcher, which misses events on some mounts); a binding that fails never stops
  the host or another binding, and a loop that crashes is restarted by the next pass.
- The same exe is the service AND the verbs: `Program.cs` sends every verb of `AgentCli.Verbs`
  (`add-binding` … `update`, `purge`) to `AgentCli`, anything else to the host.
- Log messages are `[LoggerMessage]` source-generated, French, and follow `secrets-and-logging.md`.

## The service as installed (Phase 7, 2026-10-17 — built, not yet run on Windows)

- **Name `PayrollSageAgent`** (`ServiceIdentity.Name` — one constant, the scripts repeat it), display
  « IGF Paie - Agent Sage », delayed automatic start, recovery restart/1 min, restart/1 min,
  restart/5 min, `failureflag 1`. A supervisor pass that fails is logged and retried; only something
  that escapes it sets `Environment.ExitCode = 1`, so Windows reads a failure, not a clean stop.
- **[HARD] It runs as its VIRTUAL account `NT SERVICE\PayrollSageAgent`** — never LocalSystem. It needs
  no right but its folder: the agent uses SQL logins, not Windows authentication, and calls out.
- **[HARD] Accounts are named by SID, never by name**: `*S-1-5-18` (SYSTEM), `*S-1-5-32-544`
  (Administrators — « Administrateurs » on a French Windows), and the service's SID computed from its
  name (`ServiceIdentity.Sid`, `Get-ServiceSid` in `install.ps1` — the same derivation, pinned by a test
  against TrustedInstaller's published SID).
- **[HARD] No Event Log provider.** Writing to it needs an event SOURCE an administrator registers, the
  service account cannot, and a logging provider that throws takes its caller down. `Program.cs` clears
  the default providers: console + `FileLoggerProvider` (`logs\agent-AAAA-MM-JJ.log`, 30 kept).
- **The console is UTF-8 on Windows** (`Console.OutputEncoding`): its default code page has no ’.
- **The installer is Windows PowerShell 5.1**, the one Server ships — not PowerShell 7. So: no `??`,
  ternary, `&&` / `||`, `-Parallel`; the files are written UTF-8 WITH a BOM and CRLF by
  `scripts/package.sh` (5.1 reads a BOM-less .ps1 as ANSI); a native command's stderr redirected under
  `ErrorActionPreference = 'Stop'` is a terminating error in 5.1, so `sc.exe` runs under « Continue ».
  A parse in `pwsh` (verification.md) proves syntax, not 5.1 compatibility.
- `install.ps1` is also the UPDATE: re-run from a newer zip, it stops the service, replaces the
  program, re-applies the configuration and the folder's rights, and keeps the bindings.

## The MSI (Phase 8, 2026-10-17 — built and asserted on Linux; 0.4.1 installed and online on the user's Windows Server 2025)

- **[HARD] Built on Linux: wixl, then `msibuild`** (`scripts/lib/installers.py`, in
  `scripts/packaging.Dockerfile`). wixl 0.103 refuses `ServiceConfig`, `ServiceConfigFailureActions`
  and `PermissionEx` (measured). The data folder's rights are added as Windows Installer's own
  `MsiLockPermissionsEx` (SDDL, the service named by its SID). **Delayed start, the service SID and the
  failure actions are the AGENT's job**: `PayrollSageAgent.exe configure-service`, a deferred custom
  action as SYSTEM between `InstallServices` and `StartServices` — the `sc.exe` commands `install.ps1`
  runs. **[!] They were first `MsiServiceConfig` / `MsiServiceConfigFailureActions` applied by
  `MsiConfigureServices`, and on the user's Windows Server 2025 that step failed — error 1939, « Impossible
  de configurer le service », the install rolled back (0.3.1, 2026-10-17).** Do not bring them back
  without measuring them on Windows. **The build asserts every table and action**; one missing fails the
  package.
- **[HARD] The agent's MSI steps run the exe BY PATH** — `"[INSTALLDIR]PayrollSageAgent.exe" …`, custom
  action type 34 (+ deferred, no impersonation, return ignored = 3170). They were type 18 (an exe from the
  File table) until 0.4.0, and Windows Installer then fails with **error 2753** « The File is not marked for
  installation » whenever that file is not being installed in that very transaction: on every uninstall
  (the purge — the user could not uninstall 0.3.x), and on an install where the exe is already in place
  (0.4.0 on the user's server). The build refuses any type-18 action.
- **[HARD] The upgrade code is permanent** (`UPGRADE_CODE` in `installers.py`): it is how a newer MSI
  finds the installed one. A component's GUID is derived from its file's name. A newer version
  upgrades IN PLACE and keeps `%ProgramData%` — the bindings. **[HARD] Since 0.5.0 the old product is
  removed INSIDE the new one's transaction** (`RemoveExistingProducts` at InstallInitialize + 1, asserted):
  wixl put it at 1401, after InstallValidate, where a failed upgrade leaves NEITHER version installed — the
  self-update relies on Windows Installer's own rollback. It needs the stable component GUIDs above.
- **[HARD] The purge runs on a REAL uninstall only**: a deferred, non-impersonated custom action
  (`PayrollSageAgent.exe purge --confirm`, as SYSTEM, after `StopServices`, return ignored) conditioned
  `REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE AND NOT KEEPDATA` — never during an upgrade, where the old
  product is uninstalled too, nor in the updater's rollback (`msiexec /x … KEEPDATA=1`). It unpairs and
  deletes every binding, deletes the update task and the update folder; the logs stay.
- **The services.msc LABEL is changeable, the service NAME is not.** One agent serves every SQL Server
  instance of its server (the user's choice, 2026-10-17); the label tells servers or roles apart. The
  name `PayrollSageAgent` carries the virtual account, its SID, the folder's rights and the Windows-auth
  logins. The label: `AGENTLABEL` on the MSI's command line, else the one saved in
  `HKLM\SOFTWARE\IGF\PayrollSageAgent` › `Label` (AppSearch at 50 — BEFORE the old version is removed,
  at 1401 — into its own property `AGENTLABEL_SAVED`, since AppSearch would overwrite a label given on the
  command line), else « IGF Paie - Agent Sage »; `set-label` (sc.exe + the same registry value) once
  installed; `install.ps1 -Label`, which otherwise KEEPS the current label on an update. [!] PowerShell
  variable names are case-insensitive: a local `$label` IS the `-Label` parameter — it shipped that way
  for one build, and an update would have reset the label.
- The MSI writes `HKLM\SOFTWARE\IGF\PayrollSageAgent` `InstalledBy = msi`; `install.ps1` and
  `uninstall.ps1` refuse to touch such an install. The way from the zip to the MSI is
  `uninstall.ps1 -KeepData`, then the MSI.
- **[HARD] `agent.json`, in the agent's own folder, IS the config** — the user's design (2026-10-17):
  double-click the MSI, then `Configurer-agent.bat` (Notepad, elevated), then `Redemarrer-agent.bat`.
  `init-config` (an MSI step as SYSTEM after `StartServices`) creates it empty on a first install, keeps it
  on an update, and takes an `agent.json` placed NEXT TO the MSI as the config, applied at once.
  `apply-config [--restart]` (the bat, elevated) applies it through `ConfigApplier` — desired state against
  what is linked —, writes `agent.result.txt` beside it, and restarts the service. The SERVICE never reads
  it: only the Windows-auth SQL check is handed to it (`ServiceHandOff`). The file's rights are its own:
  SYSTEM and the Administrators, re-applied at every apply (an editor that replaces the file would bring
  %ProgramFiles%'s « Users: read » back). A `.bat` is ASCII with no BOM — cmd would read the BOM as part of
  its first command. `purge` deletes `agent.json` and its report, or the program folder could not go.
- **[HARD] An `agent.json` placed NEXT TO the MSI is the config** — the user's ask: « double-click, and it
  configures through the JSON file ». `init-config --msi "[OriginalDatabase]"` (the MSI's own path, always
  set) copies it over the agent's `agent.json`, DELETES the one next to the MSI (codes, passwords), applies
  it (`ConfigApplier`, Windows-auth checks handed to the service) and writes `agent.result.txt` beside the
  MSI. It never fails the install. **It also keeps the MSI itself as `update\installed.msi`** — the version
  the updater rolls back to — and stands down when the MSI runs from the agent's own folder (its
  `agent.json` would be taken for one placed next to it).
- **Signing** is `scripts/sign.sh` and nothing else: the exe is signed BEFORE it is put in the MSI, then
  the MSI. Off (`SIGNING_ENABLED` not 1) it says « NON SIGNÉ » and the release file says
  `signed: false`.

## The self-update (Phase 9, 0.5.0 — written and compiled, not run on Windows)

- **[HARD] The updater runs from a COPY of the exe**, `update\runner-<version>\` — never from the installed
  exe, which the install replaces (locked → reboot). `configure-service` makes the copy at every install,
  writes `update\runner.txt` and registers « \IGF\PayrollSageAgent-MiseAJour » (schtasks, hourly, SYSTEM,
  `/RL HIGHEST`) on it; `update` from the installed exe refuses; `Mettre-a-jour-agent.bat` starts the copy
  that `runner.txt` names. Old runner folders are removed by the next run.
- **[HARD] The update folder is under Program Files**, never under `%ProgramData%\IGF\PayrollSageAgent`:
  the service's own account may modify its data folder, and SYSTEM must never install a file that account
  could have put there. The one thing SYSTEM reads from the data folder is `health.json`, and it can only
  trigger a rollback.
- Windows lives behind `Core.Update.IUpdateMachine`: `WindowsUpdateMachine` (msiexec, ServiceController,
  WinVerifyTrust, the MSI's registry `Version` / `InstalledBy`) and `DevelopmentUpdateMachine` off Windows,
  which stops after the verified download. The zip's `install.ps1` installs no task: a zip install updates
  by running a newer `install.ps1`.
- `LibraryImport` (WinVerifyTrust) is why the service project allows unsafe code.

## Requests the service carries out — `pending\`

The supervisor, at every pass, hands `pending\*.json` to `PendingRequests` BEFORE reconciling the loops,
so a binding it adds starts in the same pass. A verb that needs the SERVICE's identity (Windows
authentication) writes its request there and waits for `<nom>.result.json` — 25 s for the service to
pick it up (else exit 7, request withdrawn), 120 s for the answer.
