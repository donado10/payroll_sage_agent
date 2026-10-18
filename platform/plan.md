# Sage 100 agent: plan, from the empty repo to a real database

> **This is a plan, not documentation of what exists.** It is deliberately outside
> `docs/project-book/`, which describes the code as it is. When a phase ships, its facts move into
> `docs/project-book/01-backend/accounting-posting.md` in the same commit, and the phase is ticked here.
>
> Written 2026-10-17. Phase 1 of the passation comptable (file export) is built; of this plan, only
> Phase 1 (the two repos and their link) is. It lives in `docs/agent-protocol/` beside the contract,
> so `npm run agent:handoff` carries it to the agent repo's `platform/plan.md` and its progress into
> `platform/STATUS.md`.

## Goal

A way to fill the **Compte général** of a Sage 100 connection from Sage itself, instead of typing or
importing the chart of accounts. A Windows service (the **agent**) runs beside Sage, calls OUT to the
platform, reads `F_COMPTEG` from Sage's SQL Server and hands the accounts back.

Later slices (not in this plan's scope, listed at the end): writing écritures into Sage, Odoo direct
push, analytic accounts.

## Decisions already taken

| Decision | Source |
| --- | --- |
| The agent lives in a **separate repo**. The platform side and the protocol spec stay in `igf_payroll`. | user, 2026-10-13 |
| First slice is **read-only**: pull the chart into Compte général (« Synchroniser »). | user, 2026-10-13 |
| Sage 100's database is **SQL Server 2019**; comptes généraux are in **`F_COMPTEG`**. | user, 2026-10-13 |
| Tests run against a **`.bak` of a test Sage company** restored into SQL Server in Docker on this machine. | user, 2026-10-13 |
| The agent **always calls out** — HTTP for now, HTTPS later (user, 2026-10-17, Phase 7). The customer opens no inbound port. Customers' servers do have outbound internet (O4). | agreed design; user, 2026-10-17 |
| **One agent per Windows server, with many bindings** (one per database, each naming its own `SERVEUR\INSTANCE`) — *« per SQL Server INSTANCE » until 2026-10-17, when the user chose one agent for two instances on one server.* Some IT companies host several clients' Sage databases on one server, and a client may want to link theirs. | user, 2026-10-17 |
| ERP credentials stay on the customer's machine, never on the platform. | agreed design |
| Distribution: **one generic signed installer**, config separate, **never a token baked into the exe**. | agreed 2026-10-17 |
| Analytic accounts are deferred. SAP is not registered. | user, 2026-10-13 |

## Open items (block the phases that name them)

| # | What | Needed by | Who |
| --- | --- | --- | --- |
| O1 | ~~A `.bak` of a test Sage 100 company~~ **Provided** 2026-10-17: `PRESTIGE_TEXTILE.bak`, moved out of `igf_payroll` to the agent repo's `test/sage/backups/` (gitignored in both repos). Not restored yet. | Phase 2 | closed |
| O2 | ~~`F_COMPTEG`'s columns~~ **Confirmed** on the restored `.bak` (2026-10-17): `CG_Num`, `CG_Intitule`, `CG_Type`, `CG_Sommeil` exist as guessed; full shape in the agent repo's `docs/project-book/01-sage/f-compteg.md`, contract facts in `protocol.md`. | Phase 2 | closed |
| O3 | ~~Where the agent repo is cloned~~ **`/home/igf/payroll_sage_agent`**, initialised 2026-10-17 (no remote yet). | Phase 1 | closed |
| O4 | ~~Outbound internet from the Sage servers~~ **Yes** (user, 2026-10-17). | Phase 3 | closed |
| O8 | ~~Are the hosting IT companies also users of our platform?~~ **No, they only host** (user, 2026-10-17). Per-binding pairing is final; no account-level pairing. | Phase 3 | closed |
| O5 | Code-signing certificate: who buys it, which publisher name. **Deferred (user, 2026-10-17): we are in dev.** Signing is an abstracted step (Phase 8); dev builds are unsigned. Must be settled before the FIRST external customer. | Phase 8 | user, later |
| O6 | ~~Where the installer is hosted~~ **A private Storage bucket of the platform** (user, 2026-10-17). See `protocol.md` › « Distribution ». | Phase 9 | closed |
| O7 | ~~Protocol versioning policy~~ **The proposal is accepted** (user, 2026-10-17). See `protocol.md` › « Versioning ». | Phase 3 | closed |

## Design: one agent per server, many bindings

The unit on the **platform** stays the ERP connection of one société. On the **server**, one agent covers
every SQL Server instance of its Windows server; each binding names the instance it reads. The link
between the two is a **binding**.

- **Binding** = one SQL Server database ↔ one ERP connection of one société. The agent process hosts N
  bindings. Each binding is its own island: its own pairing, its own platform token, its own SQL
  credentials, its own polling loop, its own state and its own log lines.
- **[HARD] A token never spans two bindings.** One shared token for the whole agent would let a leaked
  or revoked credential touch several clients' sociétés, and would make « tied to one connection »
  false. The platform therefore sees bindings only; it has no notion of « the instance ».
- **[HARD] The platform never learns what else is on the server.** The agent does not list its
  databases to the platform, and a société's admin cannot pick a database. The IT person names the
  database locally, on the server, when adding the binding. A client's admin must not be able to
  discover that another client is hosted there.
- **[HARD] One SQL login per binding**, its credentials on the server (machine-protected, DPAPI), never
  on the platform. **Any login that reads `F_COMPTEG` is accepted, `sa` included** (the user, 2026-10-17:
  the passation comptable will write into Sage); a dedicated read-only login denied `VIEW ANY DATABASE`
  stays the recommendation on a server shared by several clients. *Read-only and « never `sa` » were
  [HARD] until then.*
- **Adding a client** = the client's payroll admin generates a pairing code in THEIR société; the IT
  person runs `add-binding` on the server with that code, the database name and the SQL login. No
  reinstall, no restart of the other bindings.
- **Removing a client** = remove the binding locally and revoke it on the platform. Both are needed,
  and the screen should say when only one has been done.
- **Failure isolation.** A binding whose database is offline, or whose token is revoked, must not
  slow or stop the others. Per-binding timeouts and backoff, and a cap on concurrent queries: this is
  a shared production server, and the agent is a guest on it.
- **One binary, one version.** An update touches every binding at once, so a hosting company may need
  a maintenance window. Auto-update can be switched off locally; the platform's minimum version still
  applies per binding and says « agent à mettre à jour ».

```mermaid
flowchart LR
    subgraph Server["Serveur de l'hébergeur — une instance SQL Server"]
        A[Agent — un service Windows]
        A --- B1[binding A<br/>base ClientA · login SQL A · jeton A]
        A --- B2[binding B<br/>base ClientB · login SQL B · jeton B]
    end
    B1 -->|HTTP(S) sortant| P1[Société A · connexion Sage]
    B2 -->|HTTP(S) sortant| P2[Société B · connexion Sage]
```

## Phase 0: Agree the plan

- [x] Read this file, strike what is wrong, answer O3 and O4.
- [x] Decide whether the agent repo's name and language are fixed (.NET, as agreed): `payroll_sage_agent`, .NET 10 (LTS).

**Exit:** this file is the agreed plan.

## Phase 1: The agent repo and the link between the two sessions

In the agent repo (new):
- [x] Create the repo at `/home/igf/payroll_sage_agent` (O3), a .NET solution (Core · Sage · Service,
      one test project), `.gitignore`, a README — organised like `igf_payroll` (`CLAUDE.md`,
      `.claude/rules/`, `docs/project-book/`).
- [x] `CLAUDE.md` saying: *the contract lives in `igf_payroll/docs/agent-protocol/`, copied into
      `platform/`; read it before touching any protocol code; never edit it from here.* Also: build and
      run .NET **in Docker** (`scripts/dotnet.sh`; no SDK installed on this Linux machine), and the
      check command (`scripts/check.sh`).
- [x] Claude Code can read `igf_payroll` without a flag: `permissions.additionalDirectories` in the
      agent repo's `.claude/settings.json`, with `Edit` denied there. The vendored copy exists too —
      `platform/`, generated, with `SHA256SUMS`.

In `igf_payroll`:
- [x] A `.claude/rules/` rule (`agent-protocol.md`) and a row in `CLAUDE.md`'s table: *touching what the
      agent calls, reads or downloads means updating the contract, freezing it (bumping the protocol
      version from v1 on) and writing an « Agent impact » entry in the CHANGELOG, in the same commit.*
- [x] `npm run check:agent-protocol` (in `precheck`) refuses a changed contract that was not frozen.
- [x] `npm run agent:handoff` copies the contract and a generated `STATUS.md` into the agent repo's
      `platform/`; a SessionStart hook there tells Claude, at every launch, whether the platform moved.

**Exit:** a Claude session in the agent repo can open the platform's spec; the platform has a rule that
stops a protocol change from landing without it.

## Phase 2: A real Sage database in Docker

- [x] Docker compose for SQL Server 2019 (in the agent repo, `test/sage/`). Developer edition.
      `test/sage/setup.sh` brings it from nothing to the state below, idempotently.
- [x] Restore the `.bak` (O1). 1 348 rows in `F_COMPTEG`. The backup is SQL Server 2019 build
      15.0.4375, one full set, `French_CI_AS`, compatibility level 140.
- [x] Read the columns (O2) and **write the real columns into the spec**: the guessed four exist; the
      facts that bear on the contract are in `protocol.md` › « `sync_gl_accounts` — the source, as
      measured », the full shape in the agent repo.
- [x] Note the collation, the `CG_Type` codes and `CG_Sommeil`: only `0` for both in this company, so
      their other values are NOT confirmed by data; `N_Nature` labels are not stored in the database.
- [x] **Two databases on the one instance** (restore the `.bak` twice under two names, or ask for a
      second company; the user agreed to either, 2026-10-17). A multi-binding design cannot be tested
      with one. Create two SQL logins, each
      read-only on its own database only. Done: `PRESTIGE_TEXTILE` / `agent_a`,
      `PRESTIGE_TEXTILE_B` / `agent_b`; measured — each reads its own `F_COMPTEG`, has no write right
      (an UPDATE is denied), cannot open or name the other database, and, denied `VIEW ANY DATABASE`,
      cannot list it. Without that deny a login with no right lists every database.

**Exit:** a reproducible command brings up SQL Server with their Sage test company; the real shape of
`F_COMPTEG` is in the spec; the instance holds two databases with a login for each.

## Phase 3: The protocol spec (in `igf_payroll`)

Written **before** any code on either side.

- [x] `docs/agent-protocol/protocol.md`: pairing, token, polling, job lifecycle (queued → claimed →
      completed | failed), job types (first: `sync_gl_accounts`), payload and result shapes, errors,
      timeouts, what a lost agent looks like, revocation and unpairing.
- [x] A machine-readable form and **golden fixtures**: the Zod contract
      `packages/app-core/src/schemas/agent-protocol.ts` (which the Phase 4 router validates with), the
      JSON Schema GENERATED from it (`schema/agent-protocol.schema.json`), 17 fixtures, one per message
      at least.
- [x] State that the **platform-side identity is the binding**: pairing, token, job claim and
      heartbeat are all per binding; no call carries a list of databases or any instance-level
      identifier shared across sociétés; one binding per connection, a new pairing replaces it.
- [x] The **protocol version** and the policy (O7): `X-Agent-Protocol` / `X-Agent-Version` on every
      call; additive where possible; the minimum enforced per binding (`protocol_unsupported`). The
      contract stays v0 through Phases 4–6 — and Phase 7, on the user's own server; v1 is declared before
      an agent is installed on a customer's (loosened from « before it leaves this machine », 2026-10-17).
- [x] `check:agent-protocol` also asserts that the JSON Schema is what the Zod contract generates, that
      every fixture parses as its message, that every message has one, and that no fixture carries a
      real-looking token.

The sync's behaviour was decided by the user on 2026-10-17: an account with no label in Sage is
skipped and reported; a `CG_Type` other than 0 is skipped and counted; « en sommeil » MIRRORS into
« désactivé » (both ways); the result is applied, then reported — no preview step.

**Exit:** both repos can be built against the same file; the fixtures validate. ✓ (2026-10-17 — the
agent side proves it in Phase 5, when its tests replay the fixtures.)

## Phase 4: Platform side (in `igf_payroll`) — built 2026-10-17

Follow the repo's own HARD rules; they are why this phase is slow on purpose.

- [x] **Invoke the `database-design` skill before writing any SQL.**
- [x] Migration `20261017100000_erp_agents.sql`: bindings (one active per ERP connection), their token
      hashes in a table of their own (`erp_agent_credentials` — no API right: on self-host RLS is the
      only gate and it hides rows, never a column), pairing codes (hashed, 15 minutes, once), jobs (one
      open per connection, lease, attempts, fence). RLS in the creating migration, `erp_agents.test.sql`
      (74 assertions), `npm run db:types`.
- [x] For each table: NOT `in_model`, NOT in `sys_snapshot_tables` (on the opt-out list with its reason)
      — but `snapshot_restore` re-issued so a restore KEEPS the binding (the user's decision); NOT in row
      history (the heartbeat would journal a row every 15 s — `sys_audit_log` instead); cascade keys, so
      `delete_company_rows()` needs no re-issue.
- [x] DEFINER functions modelled on `document_worker_*`: pair, touch, claim, complete, fail, unpair —
      service_role only, the token checked by its hash inside; the router hands the database HASHES
      only. The admin client was unavoidable (no user at all): ONE call site, `gateway.ts`, with its
      reason; `secrets-and-logging.md` says eleven.
- [x] The **first machine-authenticated route** of the API, `/api/agent` — its own error shape and
      `.onError`, 5 MB body limit, excluded from `proxy.ts`, an in-memory pairing limiter
      (`hono-api.md` › « `/api/agent` »).
- [x] The sync's write path, `agent_apply_gl_accounts`: a row by its NUMBER creates or updates; absent
      accounts untouched; **« désactivé » MIRRORS « en sommeil »** (the user's decision — this line
      first said « not changed »); `CG_Type ≠ 0`, a missing or refused label or number skipped and
      reported. `import-coverage.md` has its row.
- [x] Screen: the ERPs table's « Agent » column and its « Agent Sage » Sheet (state with a live
      heartbeat, versions, the code shown once, « Révoquer »); « Synchroniser depuis Sage » and the last
      sync's report on Compte général; Intégration › Paramètres says the pairing is ready.
- [x] Also decided and built: a DÉSACTIVÉ account still used by the month BLOCKS the entry
      (`inactive_accounts` in readiness, « Générer » refuses), for every ERP.
- [x] No new permission string (`Accounting.Setup.*` covers it); `permissions-model.md`,
      `route-index.md`, `route-map.md`, `schema.md`, `rls-policies.md`, `row-history.md`,
      `company-snapshots.md`, the chapter.

**Exit:** with a fake agent (curl and the fixtures), you can pair, claim a sync job, post accounts, and
see them in Compte général. `npm run precheck` is green. ✓ — `npm run smoke:agent` (61 assertions over
HTTP) and a browser run on the screen (pairing → « En ligne » by the heartbeat → sync → report → the
accounts in Compte général).

## Phase 5: The agent, slice 1 (in the agent repo) — built 2026-10-17

Decided first (the user, on separating concerns): the platform side became its own package,
`packages/agent-gateway`, mounted in the web app today and ready to run as a service of its own; the
agent's address is its OWN setting (`NEXT_PUBLIC_AGENT_URL`), returned by pairing as `endpoint`.

- [x] Config **per binding** — one file each in the agent's home (`bindings/<name>.json`): address,
      binding id, société and connexion, SQL target, the token and the SQL password PROTECTED (DPAPI on
      Windows — written, compiled, not run here; an explicitly insecure stand-in on Linux dev). Added by
      `add-binding` (which first CHECKS the login: reads F_COMPTEG, cannot write it, no `VIEW ANY
      DATABASE` — `sa` measured refused), removed by `remove-binding` (unpairs first), listed by
      `list-bindings`; the service re-reads the folder every 10 s, so neither restarts the others.
- [x] One polling loop per binding: claim, run, complete or fail; backoff 5 s → 5 min with jitter,
      426 retried hourly, 401 marks the binding revoked and ends its loop; ONE gate of two SQL queries for
      the whole agent; clean shutdown (measured on `docker stop`).
- [x] `sync_gl_accounts`: `CG_Num`, `CG_Intitule` (trimmed, `null` when empty), `CG_Sommeil`, `CG_Type`
      from `dbo.F_COMPTEG`, `ApplicationIntent=ReadOnly`, nothing filtered — the platform decides.
- [x] `X-Agent-Protocol` and `X-Agent-Version` on every call; the bearer on every call but `/pair`.
- [x] Logs carry the binding's local name and counts only — asserted by a test, and measured on the
      end-to-end log: no token, no password, no account number.
- [x] Tests replay the platform's fixtures: every file of `platform/fixtures` round-trips through its C#
      type, every schema message has one (35 tests in all).
- [x] Isolation: binding A's token cannot claim B's job (platform side, `erp_agents.test.sql`); each
      login reads its own database only (`test/sage/sql/login.sql`, measured in Phase 2); a dead database
      on A leaves B syncing and a revoked A stops only A (`BindingLoopTests`).

**Exit:** run in Docker against the Phase 2 database and a local platform stack, the agent pairs and a
« Synchroniser » click fills Compte général. ✓ — two bindings on one instance, 1 348 accounts each
synced at once, a second sync unchanged, a revocation stopping its loop, an unpair while running
(the agent repo's `docs/project-book/01-agent/bindings-and-verbs.md`).

## Phase 6: End-to-end on this machine — done 2026-10-17

- [x] Local Supabase stack, a platform dev server on it (port 3500, a throwaway worktree), the agent in
      Docker on the host network, SQL Server 2019 in Docker with the company restored twice.
- [x] The whole path in a browser, as alice: the « Agent Sage » sheet, its code and its address → paired
      by `add-binding` → the sheet turns « En ligne » by itself → « Synchroniser depuis Sage » → 1 348
      accounts in Compte général → a closed month mapped to synced accounts reads ready; with one of them
      put « en sommeil » in Sage and synced, the month is BLOCKED and « Générer » refuses with a sentence.
      Console clean but for the designed 400 of each refusal (`accounting-posting.md` › On screen).
- [x] Two bindings on one agent, two sociétés: requested together, each served by its own loop; then
      in turn. 1 348 accounts each; a user of one société sees none of the other's.
- [x] Every failure case, each a French sentence on screen AND in the agent's log:

| Case | On screen | The agent |
| --- | --- | --- |
| wrong code | — (`add-binding`) | « Refusé par la plateforme : Ce code d'appairage est inconnu, expiré ou déjà utilisé… » exit 4 |
| expired code | — | the same sentence: one answer for every bad code |
| revoked agent | the sheet: « La dernière liaison a été révoquée le … par … »; « Synchroniser »: « Aucun agent n'est relié à … : générez un code d'appairage dans ERPs › Agent Sage. » | « révoquée ou inconnue de la plateforme. Elle est arrêtée… »; its file `revoked`; not restarted with the service |
| agent offline | « Hors ligne », « Dernier contact il y a 2 min »; a sync asked meanwhile says the agent is off and runs 11 s after it returns | — |
| SQL Server unreachable | « Nouvel essai prévu — Le serveur SQL ne répond pas. », then after 3 attempts « La dernière synchronisation a échoué le … » | « échec signalé à la plateforme (sql_unreachable) — un nouvel essai est possible. »; a server back before the retry → the retry completes |
| odd Sage rows | « 1 mis à jour (dont 1 désactivé), 1344 inchangés, 3 ignorés », the three by reason (label `=…`, empty label, type ≠ 0) | — |
| a second sync | « 0 créé, 0 mis à jour, 1348 inchangés » | — |
| stale agent version | « À mettre à jour », « v0 — la plateforme sert les versions 1 à 1 » (platform raised to v1 in the worktree only) | « cet agent parle le protocole 0, la plateforme sert 1 à 1. Mettez l'agent à jour ; nouvel essai dans une heure. » |

Fixed on the way: the waiting sentence above (platform); a C# `True` in a French log line (agent); the
test instance's restore script, which printed « restaurée » over a failure and could not replace a
database the agent was connected to (agent repo, `test/sage/sql/restore.sql`). Learned: **Sage's own
triggers refuse a write to `F_COMPTEG` from outside Sage** (they call `master..xp_CBIsFileLock`, which only
a Sage install provides) — the test copy was edited with them paused, and the later WRITE slice must not
plan on plain SQL inserts (the agent repo's `01-sage/f-compteg.md`). The agent log: no token, no password,
no account number, no code.

**Exit:** every failure case reads as a sentence, not a stack trace. ✓

## Phase 7: Windows service and installer — done 2026-10-17 (closed by the user)

The user's answers: Windows Server 2025; a zip they install themselves, on a server with SQL Server and
Sage already there; a self-contained exe and PowerShell scripts are fine; **HTTP allowed for now, in
development and production — HTTPS later**; and **pairing over the WAN** as well as the LAN.

Built in the agent repo (`scripts/package.sh` → `dist/PayrollSageAgent-0.2.0-win-x64.zip`, 37 MB,
cross-published from Linux; its `docs/project-book/03-windows/installation.md`):

- [x] A Windows service: `PayrollSageAgent`, « IGF Paie - Agent Sage », delayed automatic start, under
      its VIRTUAL account `NT SERVICE\PayrollSageAgent` (no password, no right but its folder);
      restarted by Windows after a failure (1 min, 1 min, 5 min; `failureflag 1`).
- [x] `install.ps1` registers it ONCE per server and takes the first binding's settings if given;
      re-run from a newer zip, it UPDATES and keeps the bindings. The data folder
      (`%ProgramData%\IGF\PayrollSageAgent`) is opened to SYSTEM, the Administrators and the service
      account only, named by SID — DPAPI in machine scope makes that ACL what keeps the secrets; the
      agent sets the same rights itself when `add-binding` creates the folder first.
- [x] `uninstall.ps1` unpairs every binding, deletes its token and SQL password, the service and the
      program (`-KeepLogs` keeps the logs).
- [x] Logs IT can send: `logs\agent-AAAA-MM-JJ.log`, one per day, 30 kept; no Event Log (its source
      needs an administrator the service account is not).
- [x] Found on the way: **pairing returned `localhost`** to an agent on another machine (fixed on the
      platform — `accounting-posting.md` › « The address called »); a self-signed SQL Server certificate,
      the case of most Sage servers, now reads « reliez avec --trust-server-certificate » instead of
      « ne répond pas »; `add-binding` refuses a platform address pointing INWARD (localhost, or private
      from a public one) and undoes the pairing.
- [x] **First run on the user's Windows Server 2025** (0.2.0, the same day): `install.ps1` installed the
      service; `add-binding` over the WAN (`http://…dyndns.org:53231/api/agent`) to a named SQL Server
      2019 instance stopped at the SQL check with « Le serveur SQL ne répond pas » — and nothing else: the
      agent replaced SQL Server's error with its sentence, and the verbs wrote no log. **0.2.1**: every
      refusal prints SQL Server's own error on a « Détail : » line; every verb's output also goes to the
      log file (its pairing code masked); `check-sql` runs the check alone, with no code to expire; the
      sentences name the case (instance not found, TCP/IP refused, no answer while connecting, login).
- [x] **Any SQL login is accepted, `sa` included** (the user, same day: the passation comptable will
      write into Sage) — contract v0 step 7. The check refuses only a login that cannot read `F_COMPTEG`
      and SAYS what else it can do; a dedicated read-only login stays the recommendation on a shared server.
- [x] **The SQL check, then the WAN, on the user's server** (0.2.1, same evening). The « Détail : » line
      showed SQL Server's error 233 over shared memory; its error log said « The password of the account
      must be changed » — the login had been created with « must change at next login ». Reset by IT
      (and expiration switched off); `check-sql` passed; **paired over the WAN**
      (`http://…dyndns.org:53231/api/agent`, the platform's dev server — the fixed « address called »
      returned that address, and the agent has used it since); **« En ligne »**; **first sync: 589
      accounts received, 525 created, 64 skipped (all `CG_Type` ≠ 0, Sage's total accounts), 6
      désactivé** (« en sommeil » in Sage), in 7 s.
- [ ] **Not run on Windows — the user closed the phase on the WAN result** (« I trust the test »,
      2026-10-17): a reboot (back « En ligne » by itself), a revocation, `uninstall.ps1`. Proven on Linux
      only (the loop's revocation, Phase 5–6); the first real run of each is still to come.
- [ ] **Queued for the next build**: error 233, 18488 and 18487 (password must be changed / expired)
      get their own sentence instead of « ne répond pas »; `compte-sql-lecture.sql` switches expiration
      off on an EXISTING login too; SQL messages are localised on a French Windows, so recognition stays
      by number or by the untranslated « error: NN ».

**Before the first customer** — the user's own words: « we will adjust at the end » (2026-10-17). Each was
relaxed on purpose for the development phase:

| Relaxed now | Before a customer |
| --- | --- |
| HTTP accepted to any host | HTTPS, and the agent refuses HTTP again |
| any SQL login, `sa` included | the user liked the read-only, dedicated login: decide whether `add-binding` requires it again |
| the whole dev app published on the WAN (`:53231` fronts `next dev`) | only `/api/agent/*` behind a reverse proxy, the app on the LAN (`accounting-posting.md`) |
| contract v0, a draft | v1 declared (`protocol.md` › « Versioning ») |
| an unsigned exe | signing (Phase 8) — and with it, the updater installs only an MSI of the same publisher (Phase 9: until then it checks the SHA-256 alone, which over HTTP is as trustworthy as the network) |
| automatic updates ON by default, 03:00–05:00 | decide the default for a hosting company (`"auto": false` and its own window), and set the minimum-version window (`protocol.md` › « Versioning ») |



**Exit:** installs and uninstalls cleanly on a Windows machine, survives a reboot, pairs with a code.

## Phase 8: MSI and signing (signing abstracted) — built 2026-10-17

Decided first (the user, same day): **an MSI with a JSON config file** rather than always the zip; the
MSI built on Linux if msitools can (it can: measured, see the agent repo's
`docs/project-book/03-windows/installation.md`); **both** a SQL password and **Windows authentication**,
because the service may run on one server and read Sage on another. No certificate is bought yet.

- [x] **The MSI** — `PayrollSageAgent-<v>-x64.msi`, built by `scripts/package.sh` in a Linux container:
      wixl, then what wixl cannot express added with msibuild — the data folder's rights by SID
      (`MsiLockPermissionsEx`), the agent's own `configure-service` as SYSTEM (delayed start, the service
      SID, restart on failure; **first Windows Installer's MsiServiceConfig tables, which failed on the
      user's server with error 1939 — 0.3.1 — and were replaced by the sc.exe commands the zip had run
      there, 0.3.2**) — and a purge on a REAL uninstall (never on an upgrade): every binding
      unpaired, its secrets deleted, the logs kept. The build ASSERTS each table. A permanent upgrade
      code: a newer MSI upgrades in place and keeps the bindings. The zip stays, as the dev fallback;
      `uninstall.ps1 -KeepData` moves a zip install to the MSI without unpairing.
- [x] **ONE signing step** (`scripts/sign.sh`) behind `SIGNING_ENABLED`, off by default: off, it says
      « NON SIGNÉ » for each file; on, it signs the exe (before it goes into the MSI) and the MSI. The
      signer is replaceable — a PKCS#12 certificate with osslsigncode by default, or any command (a
      hardware token, a cloud key with jsign). **Measured on with a throwaway self-signed certificate:
      both signatures verify.**
- [x] **Unsigned builds say so**: `PayrollSageAgent-<v>.release.json` beside the deliverables —
      version, protocol, `signed`, « Version de développement, non signée », each file's SHA-256. Not the
      contract's manifest yet: Phase 9 defines that (`protocol.md` › « Distribution »).
- [x] **A JSON config file the SERVICE consumes**: dropped into `pending\`, read and DELETED at once (it
      may hold a code and a password), carried out under the service's identity, answered in
      `<nom>.result.json` and the log. `add-binding --config <fichier>` reads one at the console and asks
      the missing password. Measured on Linux: a dropped file paired and synced 1 348 accounts; a file
      with a single backslash refused with the hint; nothing left on disk.
- [x] **Windows authentication** (`--windows-auth`, `"auth": "windows"`): no password; the check is
      HANDED to the service (it must run as the identity that will read Sage), the verb prints the
      service's answer, or « le service ne répond pas » (exit 7). Measured on Linux as far as Linux goes:
      the hand-off, the service's answer (an SSPI refusal — there is no Kerberos in the container).
- [x] The SQL sentences queued from the user's server: 233, 18488, 18487, and SSPI / untrusted domain.
- [x] **Several bindings in ONE file** (the user, same day — a hosting company's server): shared values at
      the top, a `bindings` list, each with its own name, code and database, overriding the rest. The file
      is checked WHOLE first (one malformed binding: nothing done, every problem said at once); then each
      binding succeeds or fails on its own. Measured: a malformed file refused with both problems; three
      bindings in one file, one with a bad code — the two others paired and their syncs ran at once.
- [x] **One agent for two SQL Server instances on one server** (the user's choice over one agent per
      instance), and **its services.msc label** changeable: `msiexec … AGENTLABEL="…"`, kept across
      upgrades (read back from the registry before the old version is removed), `set-label` once
      installed, `install.ps1 -Label`. The service NAME stays `PayrollSageAgent`. Agent 0.3.1.
- [x] **`agent.json` NEXT TO the MSI — double-click, nothing to type** (the user, insisting: « why do I
      still have to use CMD when I asked you JSON file »). After the service starts, the MSI runs the
      agent as SYSTEM: the file is handed to the service, its answer written next to the MSI
      (`agent.result.txt`), `agent.json` deleted. A new `agent.json` and a second double-click link more.
      Measured on Linux with the exact command (two databases, 8.5 s; the service stopped: file kept).
      Agent 0.3.3. Only Windows authentication still needs IT's grant in SSMS — SQL Server's own permission.
- [x] **`agent.json` IN THE AGENT'S FOLDER, as the config** (the user's design, same evening): the MSI
      installs and creates it empty; `Configurer-agent.bat` opens it (Notepad, elevated);
      `Redemarrer-agent.bat` applies it — desired state against what is linked: new, re-paired, updated,
      `"remove": true`, left alone —, restarts the agent and shows the answer (`agent.result.txt`). Codes
      used and passwords protected leave the file. An `agent.json` next to the MSI is taken as the config.
      The SQL files for the agent's account sit beside it. Measured on Linux, every case. Agent 0.4.0.
- [x] **Error 2753 on the user's server** (installing 0.4.0; and why 0.3.x would not uninstall): the
      agent's MSI steps were « run the exe from the File table » (type 18), which Windows Installer refuses
      when that file is not being installed in the same transaction. 0.4.1 runs the exe by its path (type 34).
- [ ] **On Windows, first install of the MSI**: that Windows Installer applies the added tables
      (`sc qc`, `sc qfailure`, `icacls` on the data folder), that wixl's long-only file names install,
      the upgrade from 0.3.0 to the next, the purge on uninstall, Windows authentication for real.

**Gate before the first external customer:** the signing step on, with a real certificate (O5).

**Exit (dev):** the pipeline produces an unsigned installer and says it is unsigned. ✓

## Phase 9: Download in the app, and updates — built 2026-10-18

Decided with the user (2026-10-18): the agent installs updates BY ITSELF, in a window of hours set in
`agent.json` (03:00–05:00 by default, `"auto": false` to keep the gesture), at once when the platform
refuses its version, and rolls back on its own; a person can install now (`Mettre-a-jour-agent.bat`).

- [x] **The releases are platform data.** `npm run agent:publish` (igf_payroll) takes a version from the
      agent repo's `dist/` — its `release.json` — checks the MSI's size, SHA-256 and protocol, uploads it
      to the private bucket **`agent-releases`** (`<version>/<file>`, no société folder: it holds none)
      and writes its row in **`sys_agent_releases`** (`20261018100000`). No binary is committed. Published
      once; `--withdraw`, `--minimum`, `--list`.
- [x] **The version manifest and the download** are two routes that check neither the protocol nor the
      minimum — the way out: `POST /update` (`UpdateRequest`: the agent's settings and last attempt,
      recorded on the binding; `UpdateResponse`: latest, minimum, `outdated`) and `POST /update/download`
      (the MSI, streamed, logged in `sys_events`). On the screen, « Télécharger l'agent X.Y.Z »
      (`Accounting.Setup.Update`, `agent_installer`, logged, a signed URL under the bucket's policy).
- [x] **The config the screen hands over is the ENTRY of `agent.json`**, with the code filled in — the
      user's own flow of Phase 8 (double-click the MSI, Configurer-agent.bat, Redemarrer-agent.bat)
      replaced the « generated file next to the MSI » this line used to plan; it still works, by hand.
      Adding a société to an agent already installed is the same entry, pasted into its `bindings`.
- [x] **Auto-update in the agent** (0.5.0): `PayrollSageAgent update`, run every hour by the scheduled
      task « \IGF\PayrollSageAgent-MiseAJour » (SYSTEM, created by `configure-service`) from a COPY of the
      exe (`update\runner-X.Y.Z\` — an install replaces the program's own exe); checks through every
      binding, decides (window, `outdated`, 20 h after a failure), downloads into the update folder under
      Program Files (only SYSTEM and the Administrators write there), verifies size and SHA-256 (and, once
      signed, the publisher), installs with msiexec, waits 5 minutes for the new service to reach the
      platform (`health.json`), and otherwise rolls back from `previous.msi` (`msiexec /x … KEEPDATA=1`, then
      the version before). The MSI now removes the old version INSIDE the new one's transaction
      (RemoveExistingProducts after InstallInitialize): a failed upgrade is rolled back by Windows
      Installer itself — until 0.4.x it left neither version.
- [x] **Minimum-version enforcement**: `agent_outdated` (426, `minimumVersion`) on `/pair` and the job
      routes below the published minimum, recorded first; « À mettre à jour » and the version refused on
      the screen; the agent installs at once.

**Proven (2026-10-18):** `smoke:agent` (the update routes, the minimum, the person's download); the real
agent in Docker against the CLI stack — a build 0.4.9 told of 0.5.0, the 39 MB MSI streamed and verified
byte for byte, then refused as below the minimum and updating out of its window; the sheet in a browser
(the download byte-identical to the build). **Not run on Windows**: the task, msiexec, the health wait and
the rollback — 0.5.0 installed by hand on the user's server, then 0.5.1 published, is their first run.

**0.5.1, published on self-host 2026-10-18 (not as a minimum)**, is that first run, and it carries contract
step 11: the service says « je m'arrête » (`POST /offline`) when it stops — asked for after the user stopped
the agent and the screen still said « En ligne ». The user's server had 0.5.0 installed by hand at that
point. To see on Windows: the 0.5.0 updater installing 0.5.1 (in its window, or at once with
`Mettre-a-jour-agent.bat`), then a `net stop` read « Hors ligne » on the next reload.

**Exit:** a fresh machine goes from the app's button to a paired, online agent; an old agent updates
itself or is refused clearly.

## Phase 10: Against a real Sage database

- [ ] Run on a machine with a real (test) Sage 100 company, not the Docker restore.
- [ ] Check the chart synced matches Sage's own list of comptes (counts, a sample of numbers and labels,
      accents, the `CG_Type` handling).
- [ ] While there, **use the opportunity to verify the Sage file export** that phase 1 produces: import
      a generated écriture file into Sage and record what it accepts or rejects. Turn the result into a
      reference file in `scripts/check-accounting-connectors.mjs`. Today the layout is an assumption.
- [ ] Update `accounting-posting.md`: remove the « assumptions » warning only for what was actually
      verified.

**Exit:** the chart syncs from a real Sage company; the Sage file layout is verified or corrected.

## Afterwards (not planned in detail)

1. **Writing écritures into Sage** through Objets Métiers (COM, Windows-only). Needs its own plan: it
   writes to the customer's books, so it needs dry-run, idempotency and a reversal story.
2. **Odoo direct push**: encrypted credentials, a Hono router, a delivery table.
3. **Analytic accounts**, only when asked. The payslip freezes the établissement by id but the service
   only as a name, so the clôture would have to freeze the service id first.
4. **SAP**: say Business One or S/4HANA first.

## Standing rules while following this plan

- Docs move in the same commit as the code (`docs-and-truth.md`).
- `npm run precheck` before calling any platform phase done.
- A change to the protocol: contract, freeze (bump from v1), « Agent impact » entry and
  `npm run agent:handoff`, in the same commit (`.claude/rules/agent-protocol.md`).
- Commit only when asked.
- If the code disagrees with this plan, the code wins, and this file is corrected.
