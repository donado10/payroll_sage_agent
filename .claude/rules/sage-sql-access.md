---
paths:
  - "src/PayrollSageAgent.Sage/**"
  - "test/sage/**"
---

# Reading Sage's SQL Server

The agent is a GUEST on a production SQL Server that may host several clients' Sage databases.

- **[HARD] The CODE only reads.** It never issues `INSERT`, `UPDATE`, `DELETE`, DDL or a stored
  procedure that writes. Writing écritures into Sage is a later slice, through Sage's own route (its
  triggers refuse a plain SQL write — `01-sage/f-compteg.md`), never through SQL here.
- **The LOGIN is IT's choice: any login that reads `F_COMPTEG`, `sa` included** — the user's decision of
  2026-10-17, because the passation comptable will write into Sage. This rule said « [HARD] Read-only …
  `SELECT` on its one database and nothing else … Never `sa` » until then. What the login can do beyond
  reading is SAID at `add-binding` / `check-sql`, never refused.
- **Windows authentication** (`auth: windows`, since 0.3.0): `IntegratedSecurity`, no user, no password —
  the SERVICE's identity: `NT SERVICE\PayrollSageAgent` on its own machine, the machine account
  `DOMAINE\SERVEUR$` towards another server of a domain; impossible across machines outside a domain
  (SSPI — the sentence says so). **[HARD] Its check runs AS THE SERVICE** (`pending\`), never as the
  administrator at the console, who is a different identity. Every binding of one agent shares that
  identity: each database IT grants it is open to all of them.
- **[HARD] One binding, one database.** The connection string names its database; the code never
  switches database (`USE`, three-part names) — with `sa`, that is the only thing keeping a binding to
  its own client's books.
- **[HARD] Never enumerate the instance.** No `sys.databases`, no `sp_databases`, nothing that lists
  what else is hosted — not even to help a person choose. The IT person types the database name.
- **Recommended, no longer required: a dedicated login DENIED `VIEW ANY DATABASE`** — measured: without
  it, a login with no right at all lists every database of the instance, because `public` holds that
  permission. On a server shared by several clients it is the SQL-level separation between them.
  `test/sage/sql/login.sql` is the shape; `installer/windows/compte-sql-lecture.sql` offers it to IT.
  *[HARD] until 2026-10-17.*
- **Be a light guest.** Short queries, a command timeout on every one (connect 15 s, command 30 s), a
  global cap on concurrent queries across bindings (`JobRunner`'s gate: TWO for the whole agent), no
  long transactions, no locking hints that block Sage's users. The session declares
  `ApplicationIntent=ReadOnly` and reads four columns of `dbo.F_COMPTEG`, nothing else.
- **[HARD] `add-binding` CHECKS the login before pairing** (`SageChartReader.CheckAsync`, also alone as
  `check-sql`): it connects and can read `F_COMPTEG`, or it is refused. Whether it can also write, or see
  every database, is asked as permissions (`HAS_PERMS_BY_NAME`, never by listing anything) and REPORTED
  (`SqlLoginRights`). `sa` was measured accepted, with both lines. A failing login costs no pairing code.
- **A failure says what to look at, and keeps SQL Server's words** (`SageChartReader.Name`): instance not
  found, TCP/IP refused, name not resolved, no answer in 15 s while CONNECTING (not « la lecture a dépassé
  son délai »), certificate, TLS, login — recognised by the SNI error the message names (« error: 26 »),
  which is the same text under Windows' native network layer and the managed one, where the NUMBER is
  not. SQL Server's own error is `SageReadException.Detail`: printed under the sentence and written to the
  log file, never sent to the platform. The first real server (2026-10-17) showed why: « Le serveur SQL ne
  répond pas » alone could not be diagnosed — its detail was error 233, a login created « must change
  password »; 233 (shared memory), 18488 and 18487 (TCP) now say it. **On a French Windows, SQL Server's
  messages are FRENCH** (« Fournisseur de mémoire partagée ») — recognise by number, or by the « error:
  NN » the SNI text keeps in English; never by English prose.
- **Sage's column names are FACTS to measure, not to remember.** `F_COMPTEG` was measured on
  2026-10-17 (`docs/project-book/01-sage/f-compteg.md`); any other table is an assumption until it is
  measured the same way. What a code MEANS (`CG_Type`, `CG_Sommeil` other than 0, `N_Nature`) is not
  in the database and is not to be guessed.
- **Trim `CG_Intitule`**: a quarter of the measured labels carry spaces.

## The test database

`test/sage/setup.sh` builds the SQL Server 2019 container with two databases and two read-only
logins (`agent_a`, `agent_b`); `test/sage/sqlcmd.sh` queries it. The `.bak` files in
`test/sage/backups/` are a client's books: gitignored, never read into the conversation, never
copied elsewhere. **Query as the binding's login, not as `sa`**, unless the task is the setup itself.
Report structure, counts and distinct codes — never account contents.
