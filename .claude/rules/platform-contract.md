# The platform contract

**The platform owns the contract; this repo implements it.** The contract is
`igf_payroll/docs/agent-protocol/`, and `platform/` here is a copy written by
`npm --prefix /home/igf/igf_payroll run agent:handoff`, with:

| File | What |
| --- | --- |
| `platform/protocol.md` | the contract in prose — routes, payloads, jobs, errors, versioning, distribution |
| `platform/version.json` | `protocol` (the contract's version) and `minimumSupported` |
| `platform/schema/`, `platform/fixtures/` | the machine-readable form and the golden exchanges (from Phase 3) — the tests replay the fixtures |
| `platform/CHANGELOG.md` | one entry per version, each with an « Agent impact » written for THIS session |
| `platform/plan.md`, `platform/STATUS.md` | the step-by-step plan and its progress, generated |
| `platform/source.json`, `platform/SHA256SUMS` | where the copy came from, and what `scripts/check.sh` verifies |

## [HARD] Never edit `platform/`, never edit igf_payroll

Both are denied in `.claude/settings.json`. A subprocess could still write there; do not. If the agent
needs the platform to change — a field, a route, a refusal — **say so to the user, precisely**, and it
is made in igf_payroll's session, where its own rule (`agent-protocol.md`) gives it a version and an
« Agent impact ». Then the handoff brings it here.

## When the platform moved

`scripts/platform-status.sh` runs at every session start and its note arrives in your context,
prefixed `[platform]`:

| Note | Do |
| --- | --- |
| « In sync » | nothing |
| « THE CONTRACT CHANGED » | before any protocol work: read the listed « Agent impact » lines (and `igf_payroll/docs/agent-protocol/CHANGELOG.md` above the version in `platform/`), run the refresh command, then **tell the user what must change in this repo** before changing it |
| « NOT FROZEN yet » | igf_payroll is mid-change; keep working from `platform/` and do not implement the moving part |
| « the plan or the CHANGELOG moved » | refresh when convenient; nothing to implement |
| « EDITED BY HAND » | restore with the refresh command; if the edit was meant, it goes to igf_payroll |
| « igf_payroll was not found » | work from `platform/`; freshness is unknown, say so if it matters |

## The contract, in code

`Core/Contract/Messages.cs` holds one C# record per message, named as the schema's `$defs`, with the
contract's property names. `ContractFixturesTests` replays EVERY file of `platform/fixtures` through
its type and back, and asserts that every message of the schema has a type here — so a field renamed
on the platform fails `scripts/check.sh` after the next handoff, not at a customer's. Codes are strings,
not enums, and an unknown key in a response is ignored: the tolerant reader the contract asks for.

**The address.** The agent calls `PairResponse.endpoint` — the address pairing confirmed, kept in the
binding file — for every call after pairing, never the URL typed at `add-binding` again. One exception,
the agent's own safety rule rather than the contract's: an address that points INWARD from where the
platform was reached is refused and the pairing undone (`AgentAddress.MayFollow`,
`secrets-and-logging.md`). Since step 6 of v0 the platform returns the address the agent CALLED; a
binding paired from another machine before that fix holds `localhost` and must be paired again.

## The version this agent speaks

`AgentProtocol.Version` (`src/PayrollSageAgent.Core/AgentProtocol.cs`) is the contract version the
code implements. It moves when — and only when — the code implements a new version. A test asserts
`minimumSupported ≤ AgentProtocol.Version ≤ protocol` against `platform/version.json`: an agent that
the platform no longer serves fails here before it fails at a customer's.
