# The development environment

## Summary

The development machine is Linux with Docker and Node, and no .NET SDK. The SDK runs in a container
(`scripts/dotnet.sh`), and the test Sage database runs in a SQL Server 2019 container
(`test/sage/`). Nothing Windows-only runs here — see `.claude/rules/verification.md`.

## .NET, in a container

| | |
| --- | --- |
| Image | `mcr.microsoft.com/dotnet/sdk:10.0` (override: `DOTNET_SDK_IMAGE`) — 10.0.402 when this was written |
| SDK pin | `global.json`: `10.0.100`, `rollForward: latestFeature` |
| Mount | the repo at `/repo`, run as your uid, so `bin/` and `obj/` stay yours |
| Packages | `.nuget-cache/` in the repo (gitignored) — the first restore downloads, the next ones do not |

```bash
scripts/dotnet.sh build PayrollSageAgent.slnx
scripts/dotnet.sh test PayrollSageAgent.slnx
scripts/run-service.sh                                          # builds, runs; Ctrl+C stops it
```

**Run the service with `scripts/run-service.sh`, not `dotnet run`.** In the container, `dotnet run`
does not pass Ctrl+C on to the app: the client exits and the container keeps running (measured
2026-10-17). The script runs the built dll, which stops cleanly and takes the container with it.
`scripts/dotnet.sh` adds `--init`, so the signal reaches the process at all.

The service runs in the container exactly as on Windows minus the service registration:
`AddWindowsService` does nothing off Windows.

## The test Sage database (Phase 2 — built)

`test/sage/docker-compose.yml` runs SQL Server 2019, **Developer edition** (free for development and
testing, not for production), container `payroll-sage-sql`, on `127.0.0.1:14330` only. The backups
are in `test/sage/backups/`, mounted read-only — **gitignored, a client's books**.

```bash
cp test/sage/.env.example test/sage/.env      # three passwords: sa, agent_a, agent_b
test/sage/setup.sh                            # from nothing to the state below; idempotent
test/sage/setup.sh --replace                  # restore again over the existing databases — even with
                                              # the agent running: it ends the sessions on that database
```

`setup.sh` leaves the instance as two bindings of a hosting company would see it:

| Database | Login | Rights |
| --- | --- | --- |
| `PRESTIGE_TEXTILE` | `agent_a` | `db_datareader` on this database only; `DENY VIEW ANY DATABASE` |
| `PRESTIGE_TEXTILE_B` (same backup, second name) | `agent_b` | the same, on its own |

Measured on 2026-10-17, as `agent_a`: it reads its `F_COMPTEG` (1 348 rows); it holds no INSERT,
UPDATE, DELETE or ALTER right, and an UPDATE is refused (Msg 229); a three-part name into the other
database is refused (Msg 916) and so is connecting to it; it lists only `master`, `tempdb` and its
own database. **A login with no right and without the deny listed all six databases of the
instance** — that is why the deny is in `sql/login.sql`, and in the contract.

`test/sage/sqlcmd.sh` is sqlcmd inside the container, as `sa` or as a binding's login
(`SQL_USER=agent_a SQL_PASSWORD_VAR=AGENT_A_PASSWORD`); passwords travel as environment variables,
never on a command line. `-C` trusts the container's self-signed certificate — test only.

**Report structure and counts from it, never account contents** (`.claude/rules/sage-sql-access.md`).
The measured shape of the chart is `01-sage/f-compteg.md`.

## End to end — the agent against Sage and the platform

Run on 2026-10-17 (results: `01-agent/bindings-and-verbs.md` › « Measured end to end »). What it needs:

1. **The test Sage instance**: `test/sage/setup.sh` — two databases, `agent_a` / `agent_b`.
2. **A platform on the CLI stack**, never the user's self-host: igf_payroll's verify recipe — a
   worktree at `/home/igf/igf_payroll_verify_agent`, its `.env.local` from `npx supabase status -o env`,
   `npx next dev -p 3500 -H localhost`. A Sage ERP there and a pairing code (its « Agent Sage » sheet,
   or the RPC as alice).
3. **The agent, in Docker on the host network**, with a home inside the repo and the dev secrets:

   ```bash
   export DOTNET_DOCKER_NETWORK=host PAYROLL_AGENT_HOME=/repo/.agent-home PAYROLL_AGENT_DEV_SECRETS=1
   A=src/PayrollSageAgent.Service/bin/Debug/net10.0/PayrollSageAgent.dll
   scripts/dotnet.sh build src/PayrollSageAgent.Service
   set -a; . test/sage/.env; set +a
   printf '%s\n' "$AGENT_A_PASSWORD" | scripts/dotnet.sh $A add-binding --name prestige-a \
     --url http://localhost:3500/api/agent --code XXXX-XXXX-XXXX --sql-server "127.0.0.1,14330" \
     --database PRESTIGE_TEXTILE --sql-user agent_a --trust-server-certificate --sql-password-stdin
   scripts/dotnet.sh $A                      # the service; « Synchroniser » on the platform
   ```

4. **Afterwards**: delete the test ERP on the platform (the cascade takes the synced accounts — a
   client's chart), `rm -rf .agent-home`, and `npm run db:reset` in igf_payroll.

`.agent-home/` is gitignored: it holds bindings and their (development-protected) secrets.

## The platform beside it

`/home/igf/igf_payroll` is read through `.claude/settings.json` › `additionalDirectories`. Its own
local stack (Supabase, the web app) is needed from Phase 4 on; start it from that repo
(`local-stack.md` there).
