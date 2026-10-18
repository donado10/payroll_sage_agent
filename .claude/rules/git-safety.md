# Git

- **[HARD] Commit only when asked.** Not as a courtesy at the end of a task.
- **[HARD] Never switch branches with uncommitted work.**
- Descriptive messages: what changed and why, not "fix".
- `platform/` is committed WITH the change that needed it: a commit that implements protocol v2
  carries the v2 copy, so the history shows which contract each version of the agent was built for.

## Never committed

- **`*.bak` and `test/sage/backups/`** — a SQL Server backup is a client's books.
- `.env` and `.env.*` (except `.env.example`), `.nuget-cache/`, `bin/`, `obj/`.
- **Any certificate or key** (`*.pfx`, `*.p12`, `*.pem`, `*.snk`). The signing certificate never enters
  the repo; the release pipeline reaches it.

Verify with `git check-ignore -v <path>` rather than assuming.
