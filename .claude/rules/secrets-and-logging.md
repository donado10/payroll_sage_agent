# Secrets and logging

The agent sits on a server that may host SEVERAL clients' books, and it holds credentials for some
of them. What it stores and what it writes is the whole of its security.

| Secret | Where it lives | Never |
| --- | --- | --- |
| a binding's platform token | on the server, machine-protected (DPAPI), per binding | in a config file in clear, in a log, in an error, in a test fixture |
| a binding's SQL password | same, per binding | the same — and never a login shared by two bindings |
| a pairing code | used once, then discarded | stored after redemption |

- **[HARD] One binding never sees another's secret.** Separate storage entries, separate config
  files; a bug in one binding's code path cannot read the other's.
- **Where they are.** Each binding is one file, `bindings/<name>.json` in the agent's home, its two
  secrets PROTECTED inside it (`protectedToken`, `protectedSqlPassword`):
  - **Windows: DPAPI, machine scope** (`DpapiSecretProtector`) — the service account must read what an
    administrator's `add-binding` wrote, so what keeps other processes out is the FOLDER's ACL: SYSTEM,
    the Administrators and `NT SERVICE\PayrollSageAgent` only, no inherited right. `install.ps1` sets it
    (and re-sets it on every update); `AgentHome.Ensure` sets the same when `add-binding` creates the
    folder first — %ProgramData%'s defaults let every local user READ. Written, compiled, not run here.
  - **Linux development: `DevSecretProtector`, which says it is not secure** — AES-GCM under a key in a
    file beside the bindings. It refuses Windows, and anywhere without `PAYROLL_AGENT_DEV_SECRETS=1`.
  - Files are owner-only (600) on Unix; written atomically.
- **[HARD] The SQL password never travels as an argument** — a hidden prompt, or `--sql-password-stdin`.
- **A request file in `pending\` may hold a pairing code and a SQL password IN CLEAR** — the one place a
  password sits on disk unprotected. **[HARD] The service reads it and deletes it before doing anything
  else**, and makes ONE attempt: no retry loop with a password lying there. Its answer
  (`<nom>.result.json`) and the log carry no secret. A file given to `add-binding --config` is the
  user's and stays where it is — the verb says so when it holds a password.
- **Windows authentication stores no SQL password at all** (`ProtectedSqlPassword` empty).
- **`agent.json` holds secrets only until it is applied**: then a code used becomes « "" » and a password
  the agent protected becomes « (protégé) » (`AgentConfigFile.WithoutSecrets`) — the file stays the list of
  what is linked, never a store of secrets. Until then its own rights admit SYSTEM and the Administrators
  only, re-applied at every apply. A password typed for a binding that FAILED stays, for the next try.
- **HTTP is accepted, to any host, for now** — the user's decision of 2026-10-17, for development AND
  production (`platform/protocol.md` › « Transport »). Over HTTP the code, the token and the chart cross
  the network in clear; `add-binding` says so every time it is given an `http://` address that is not
  this machine. When HTTPS comes, `AgentCli.Address` refuses HTTP again.
- **[HARD] The token never goes INWARD.** After pairing, the agent follows the platform's address
  unless it points back at this server, or at a private address while the platform was reached at a
  public one — at a customer's, that is one of THEIR machines (`AgentAddress.MayFollow`, tested). The
  pairing is undone at once, through the address that worked.
- **The log files are what IT sends us** (`logs\`, one per day, 30 kept) — so everything above about
  what a line may carry is about a file that leaves the server. The `bindings\` folder never leaves it.
  **What a verb prints is logged too** (`LoggingWriter`), with the command line — its pairing code
  masked: so a verb must never PRINT a secret, which it never did.
- **SQL Server's own error stays on this machine** (`SageReadException.Detail`): the console and the log
  file, never the platform, which receives the French sentence. It holds no secret — SQL Server names the
  login it refused, never the password.
- **[HARD] Never log a row of Sage.** An account number in a log is a client's chart of accounts
  in a file an IT person emails to support. Log identifiers and COUNTS: « 412 comptes lus,
  binding 3f… », never the comptes.
- **[HARD] Never log or send the list of databases of the instance**, not even at debug level.
- Errors the platform or a person sees are short, French, and say what to do; the technical detail
  goes to the local log, without secrets.
- Remove debug logging before finishing.
