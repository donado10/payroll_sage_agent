# Scripts

## Summary

Everything runnable in this repo, and what it actually checks.

| Script | What it does |
| --- | --- |
| `scripts/check.sh` | **The precheck.** 1) `platform/` is exactly what the handoff wrote — `SHA256SUMS` holds and no file was added; 2) `dotnet format --verify-no-changes`; 3) build, warnings as errors; 4) tests. Cannot prove anything Windows-only |
| `scripts/dotnet.sh <args>` | The .NET CLI in `mcr.microsoft.com/dotnet/sdk:10.0`, repo mounted at `/repo`, your uid, NuGet cache in `.nuget-cache/`, stdin open. `DOTNET_DOCKER_NETWORK=host` puts it on the host network (to reach the platform's dev server and the test SQL Server); `PAYROLL_AGENT_HOME` and `PAYROLL_AGENT_DEV_SECRETS` are passed through when set. Also runs the built agent: `scripts/dotnet.sh src/PayrollSageAgent.Service/bin/Debug/net10.0/PayrollSageAgent.dll <verb>` |
| `scripts/package.sh` | One version's Windows deliverables in `dist/`: `dotnet publish` for win-x64 (self-contained, single file) in the SDK container, then — in the packaging container (`scripts/packaging.Dockerfile`: msitools, wixl, osslsigncode, python3) — `scripts/lib/installers.py`: the exe signed or « NON SIGNÉ », the MSI (wixl + msibuild, its tables ASSERTED), the MSI signed or « NON SIGNÉ », the zip, `<v>.release.json` and `.sha256` files. Signing env: `SIGNING_ENABLED`, `SIGNING_DIR`, `SIGNER`, `SIGNING_COMMAND`, `SIGNING_TIMESTAMP_URL`. Proves nothing on Windows |
| `scripts/sign.sh <file>` | The ONE signing step — run by `installers.py` inside the container, never by hand. Off unless `SIGNING_ENABLED=1`; osslsigncode with `SIGNING_DIR`'s `signing.pfx` + `signing.password`, or `SIGNER=command` |
| `scripts/run-service.sh [args]` | Builds the service and runs its dll in the SDK container. Ctrl+C stops it and the container — `dotnet run` would not (see `02-development/dev-environment.md`) |
| `scripts/platform-status.sh` | The SessionStart hook (`.claude/settings.json`). Prints one `[platform]` note: runs igf_payroll's `agent-handoff.mjs --status` when that repo is at `PLATFORM_REPO` (default `/home/igf/igf_payroll`), else describes `platform/` alone. Always exits 0 |
| `test/sage/setup.sh [--replace] [file.bak]` | Starts the SQL Server 2019 test container, restores the backup TWICE (`<name>` and `<name>_B`) and creates one read-only login per database (`agent_a`, `agent_b`) with `DENY VIEW ANY DATABASE`. Idempotent; `--replace` restores over existing databases. Passwords from `test/sage/.env` |
| `test/sage/sqlcmd.sh <sqlcmd args>` | sqlcmd in the test container, as `sa` by default or `SQL_USER` / `SQL_PASSWORD_VAR`; `SQL_VARS` passes scripting variables through the environment. Test only (`-C`) |
| `npm --prefix /home/igf/igf_payroll run agent:publish -- --stack=self --version=X.Y.Z --yes` | Not a script of this repo: igf_payroll's. Publishes a version of `dist/` — its `release.json`, its MSI checked against it — to that platform's bucket `agent-releases` and table `sys_agent_releases`; the agents then update themselves to it. `--minimum` (older agents refused until they update), `--withdraw=X.Y.Z`, `--list`; a dry run without `--yes` |
| `npm --prefix /home/igf/igf_payroll run agent:handoff` | Not a script of this repo: igf_payroll's. Rewrites `platform/` from `igf_payroll/docs/agent-protocol/`, refusing a contract that is not frozen. The only way `platform/` changes |

## Not covered

There is no CI for this repo yet (no remote). `scripts/check.sh` is run by hand. Nothing exercises
the service on Windows, the installer, DPAPI or a signed build — see `.claude/rules/verification.md`.
