# Precedence, and keeping docs true

```
platform/ (the platform's word, for anything between the two)
code → docs/project-book/ → .claude/rules/
```

**[HARD] If the code disagrees with a rule or a doc, the code wins.** Say so out loud and fix the
rule in the same change. A rule that has quietly gone false is worse than no rule, because it still
gets followed.

## [HARD] The docs change in the same commit as the code

| You changed… | Update, same commit |
| --- | --- |
| anything with observable behaviour | the `docs/project-book/` chapter covering it |
| a script in `scripts/`, or added a check | `docs/project-book/05-reference/scripts.md` and `verification.md` |
| the dev environment (Docker images, the SQL Server container) | `docs/project-book/02-development/dev-environment.md` |
| the protocol version implemented | `AgentProtocol.Version`, and `platform/` refreshed in the same commit |
| added a chapter | `docs/project-book/SUMMARY.md` |

**What the agent repo does NOT document is the contract.** That is `platform/`, and it is written in
igf_payroll. A chapter here says how the agent IMPLEMENTS it, and links to it.

- Diagrams are Mermaid, inline. No image files.
- **Never write a secret value into a doc**, and never a row of a client's Sage database.
