# payroll_sage_agent

The on-premise agent of the IGF payroll platform: a Windows service installed beside Sage 100 that
calls OUT to the platform and reads Sage's SQL Server for it. **.NET 10 (LTS)**, one solution
(`PayrollSageAgent.slnx`), built and tested **in Docker** on this Linux machine. Identifiers and
code are English; every sentence a person reads — the CLI, the event log, an error — is French,
like the platform.

**The platform is another repo**: `igf_payroll`, at `/home/igf/igf_payroll`. This session can READ
it (`.claude/settings.json` › `additionalDirectories`) and cannot EDIT it (denied). Its
`CLAUDE.md` and rules do not load here; what this repo needs from it arrives in `platform/`.

## How rules load

`.claude/rules/*.md` with **no `paths:` frontmatter** load at session start, with the same weight as
this file:

`00-orientation` · `platform-contract` · `verification` · `git-safety` · `secrets-and-logging` ·
`docs-and-truth` · `naming-conventions`

Rules **with** `paths:` load when you read a matching file — **on READ only**. They do not fire when
you write a brand-new file, work only in Bash, or plan. So:

## Read this before you touch that

| I am about to… | Read first |
| --- | --- |
| implement, change or rely on anything the platform sends or accepts | `platform/protocol.md` and `platform-contract.md` — **the contract is igf_payroll's; `platform/` is a generated copy, never edited here** |
| see a « [platform] … CHANGED » note at session start | `platform-contract.md` › « When the platform moved » — **read the CHANGELOG's « Agent impact », refresh, then tell the user before writing code** |
| need the platform to change something | `platform-contract.md` — **say it to the user; it is done in igf_payroll's session, never from here** |
| write a SQL query, open a connection to Sage, or touch `test/sage/` | `sage-sql-access.md` — **the code only reads; one login per binding (any, `sa` included); never list the databases** |
| touch the Windows service host, its install or its logs | `windows-service.md` — **nothing Windows-only can be run on this machine; say so** |
| store or log anything — a token, a password, a pairing code, a row | `secrets-and-logging.md` |
| finish _any_ change | `docs-and-truth.md` — **the docs move in the same commit**; `scripts/check.sh` green |

## [HARD] The binding is the unit

One agent per Windows **server** — every SQL Server instance on it; it hosts N **bindings**, each one
database (on the instance it names) ↔ one ERP connection of one société. Each binding has its own
pairing, its own platform token, its own SQL access (a login, or the service's Windows identity), its own
polling loop, state and log lines. **Nothing ever spans two bindings**, and nothing tells the platform
what else the server hosts. `platform/protocol.md` › « Identity: the binding » is the source.

## [HARD] Never edit `platform/`

It is written by `npm --prefix /home/igf/igf_payroll run agent:handoff` and checked by
`scripts/check.sh` against its `SHA256SUMS`. A « fix » made in the copy is lost at the next handoff
and, meanwhile, describes a contract the platform does not have.

## Verifying

`scripts/check.sh` — the platform snapshot is intact, format, build (warnings are errors), tests.
Every `dotnet` command goes through `scripts/dotnet.sh` (the SDK runs in a container). See
`verification.md` for what is NOT covered — anything that needs Windows.

## Precedence

```
code → docs/project-book/ → .claude/rules/
```

…and above all three, for anything between the agent and the platform: **`platform/`**, which is the
platform's word. If the code here disagrees with `platform/`, the code is wrong. If a rule here
disagrees with the code, the code wins — say so out loud and fix the rule in the same change.

The plan these steps follow is `platform/plan.md`; its progress is in `platform/STATUS.md`.
