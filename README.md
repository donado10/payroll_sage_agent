# Agent Sage — IGF Paie

The on-premise agent of the IGF payroll platform. A Windows service installed beside Sage 100, on
the SQL Server instance that holds the Sage databases. It calls OUT to the platform over HTTPS — no
port is opened at the customer — and reads, for each **binding** (one database ↔ one ERP connection
of one société), what the platform asks for. First job: the chart of accounts (`F_COMPTEG`) into the
platform's « Compte général ».

**Status: Phase 8 of `platform/plan.md`.** The agent pairs, keeps its bindings, runs one loop per
binding and synchronises the chart of accounts — end to end on Linux, and on the user's Windows Server
2025 over the WAN (the zip, 0.2.x). Since 0.3.0: an MSI built on Linux (`scripts/package.sh`), one
signing step (off until there is a certificate), bindings from a JSON file the service consumes, and
Windows authentication. The MSI has not been installed on Windows yet. The download and the updates are
Phase 9. Progress: `platform/STATUS.md`.

| Read | For |
| --- | --- |
| `platform/protocol.md` | what the platform expects of the agent — the contract (owned by igf_payroll) |
| `docs/project-book/` | how this repo is built, and how to run it here |
| `CLAUDE.md` | the working rules |

## Quick start (Linux, Docker)

```bash
scripts/check.sh                                          # snapshot, format, build, test
scripts/run-service.sh                                    # Ctrl+C stops it
npm --prefix /home/igf/igf_payroll run agent:handoff      # refresh platform/ from the platform
```
# payroll_sage_agent
