# Platform status — GENERATED, do not edit

> Written by `npm run agent:handoff` in `igf_payroll`. Its source is
> `igf_payroll/docs/agent-protocol/`; a change goes THERE, in the igf_payroll session.
> `scripts/check.sh` fails when a file of `platform/` no longer matches `SHA256SUMS`.

| | |
| --- | --- |
| Contract | **v0** · minimum supported v0 · `e253a1b0ba66` |
| Source | igf_payroll `b-1` @ `b603cd1` (2026-10-13) — **includes uncommitted files** |
| Exported | 2026-10-18T12:43:09.338Z |

## Plan progress (`plan.md`)

| Phase | Done |
| --- | --- |
| 0 — Agree the plan | 2/2 ✓ |
| 1 — The agent repo and the link between the two sessions | 6/6 ✓ |
| 2 — A real Sage database in Docker | 5/5 ✓ |
| 3 — The protocol spec (in `igf_payroll`) | 5/5 ✓ |
| 4 — Platform side (in `igf_payroll`) — built 2026-10-17 | 9/9 ✓ |
| 5 — The agent, slice 1 (in the agent repo) — built 2026-10-17 | 7/7 ✓ |
| 6 — End-to-end on this machine — done 2026-10-17 | 4/4 ✓ |
| 7 — Windows service and installer — done 2026-10-17 (closed by the user) | 8/10 |
| 8 — MSI and signing (signing abstracted) — built 2026-10-17 | 11/12 |
| 9 — Download in the app, and updates — built 2026-10-18 | 5/5 ✓ |
| 10 — Against a real Sage database | 0/4 |

## Open items still open

| # | What | Needed by | Who |
| --- | --- | --- | --- |
| O5 | Code-signing certificate: who buys it, which publisher name. **Deferred (user, 2026-10-17): we are in dev.** Signing is an abstracted step (Phase 8); dev builds are unsigned. Must be settled before the FIRST external customer. | Phase 8 | user, later |

## Latest contract entry (`CHANGELOG.md`)

### v0 — 2026-10-18

The draft, written in steps over two days — v0 is not frozen for anyone yet, so it was amended
rather than versioned:

1. **Skeleton**: the binding as the identity, the security invariants, the versioning (O7) and the
   distribution (O6).
2. **Phase 2's measurements**: the real shape of `F_COMPTEG` as it bears on `sync_gl_accounts`, and
   the `DENY VIEW ANY DATABASE` a binding's login needs.
3. **Phase 3, the contract SPECIFIED**: routes, headers, pairing, polling and jobs, `sync_gl_accounts`
   with the platform's rules (decided by the user: skip a label missing in Sage, skip a `CG_Type`
   other than 0, mirror « en sommeil » into « désactivé », apply then report), the agent's failures,
   the platform's refusals, timeouts, revocation; the JSON Schema generated from
   `packages/app-core/src/schemas/agent-protocol.ts`, and at least one fixture per message.

4. **Phase 4 amendments**, found while building the platform side: `complete` and `fail` lose their
   path parameter (`POST /api/agent/jobs/complete`, `/jobs/fail`, `jobId` in the body — the platform's
   API has no nested path parameters); every route may answer 400 (malformed JSON included), 413 and
   426, and a 426 on a token route is recorded first; `X-Agent-Version` has a closed form (64
   characters at most); two skip reasons are added now (`label_invalid`, `number_invalid`), because an
   enum value added later breaks a C# enum; the rate limit is global first; a frozen société hands out
   no job; the platform's own job failures are listed; a Sauvegarde restore keeps the binding.

5. **The agent's address is its own** (the user's decision on separating the agent side): a setting
   distinct from the web app's (`NEXT_PUBLIC_AGENT_URL`), shown beside the code, and returned by pairing
   as `PairResponse.endpoint` — the address the agent uses from then on. The platform side moves to its
   own package (`packages/agent-gateway`), mounted in the web app today and ready to run on its own.

6. **Phase 7: HTTP is allowed, and « the address called » is said exactly.** The user decided that
   plain HTTP is accepted to any host, in development and in production, until HTTPS is set up — the
   rule was « HTTPS, HTTP only to localhost ». And pairing returns the address the agent CALLED (its
   `Host` header), never the host the web server listens on: Next rebuilt `request.url` with
   `localhost`, so an agent paired from another machine was told to call itself (measured through
   172.16.2.4; every earlier test ran on localhost). v0 stays a draft while the only agents installed
   are on the user's own servers; v1 comes before a customer's.

7. **Phase 7, on the user's server: any SQL login.** The user decided that the binding's login may be any
   login that reads `F_COMPTEG`, `sa` included, since the passation comptable will write into Sage: the
   « read-only » and « DENIED `VIEW ANY DATABASE` » invariants are no longer required — the second stays
   RECOMMENDED on a shared instance, where it is the SQL-level separation between clients.

8. **Phase 8: Windows authentication.** A binding may sign in to SQL Server with the agent service's own
   Windows identity instead of a SQL login — no password stored anywhere; on a domain, towards a remote
   SQL Server too. Every binding of one agent then shares that identity, which the security invariant
   now says. Nothing on the wire changes: the platform never learned how a binding signs in.

9. **One agent per Windows server, all its SQL Server instances** (the user's choice, two instances on
   one server): « one agent per instance » was a description, never a rule the code kept — each binding
   names its own `SERVEUR\INSTANCE`. The agent's services.msc label may be changed; its service name may
   not.

10. **Phase 9: the agent updates itself** (2026-10-18). Two routes that check neither the protocol nor
    the version — the way out, whose shapes only ever grow: `POST /update` (`UpdateRequest` → what the
    agent says of its updates, recorded on the binding; `UpdateResponse` → the latest published version,
    the minimum, `outdated`) and `POST /update/download` (`UpdateDownloadRequest` → the MSI's bytes). A
    second floor, on the BUILD version: `agent_outdated` (426, `minimumVersion`) on `/pair` and the job
    routes when the platform publishes a minimum above the agent. `release_not_found` (404). The
    installers live in the private bucket `agent-releases`, described by `sys_agent_releases`.

11. **A service that stops says so** (2026-10-18). The user stopped the agent's service and the screen
    still said « En ligne » after a reload: the platform only learned it from silence, and « En ligne »
    meant « heard from in the last 120 s ». `POST /offline` (`OfflineRequest`, empty → `204`) makes the
    binding « Hors ligne » at once; it checks neither the protocol nor the version, like `/unpair`. The
    window for an agent that stops WITHOUT saying so (crash, power cut) is now **45 s**, three polls
    missed. Additive for the agent: one that never calls `/offline` is exactly as before, only « Hors
    ligne » sooner.

Both sides are built (Phases 4–6). v1 is declared before an agent is installed on a customer's server.

Agent impact: étape 11 — quand le service s'arrête (arrêt demandé, redémarrage, arrêt de Windows, une
mise à jour qui le remplace), APRÈS avoir arrêté et attendu les boucles des liaisons, appeler
`POST /offline` (corps `{}`, réponse `204`) pour chaque liaison dont la boucle tournait, en parallèle,
avec 5 secondes au plus pour l'ensemble ; une erreur ou un délai dépassé ne se réessaie pas et
n'empêche pas l'arrêt. Une réponse `token_invalid` ne change rien localement (la liaison a été révoquée
entre-temps : la prochaine boucle le constatera). Livré en 0.5.1.

Agent impact: étape 10 — l'agent 0.5.0 se met à jour seul. Un outil de mise à jour (le même exe,
`update`, lancé toutes les heures par une tâche planifiée SYSTEM que le MSI crée, depuis une COPIE de
l'exe dans `update\runner-<version>\` pour ne pas verrouiller les fichiers que l'installation
remplace) appelle `POST /update` pour CHAQUE liaison active (`UpdateRequest` : `auto`, `window`,
`last`), et installe `latest` quand l'agent est `outdated`, ou quand `auto` et l'heure du serveur dans
`window` — jamais une version qui a échoué dans les 20 dernières heures. Il télécharge par
`POST /update/download`, vérifie la taille et le SHA-256 de `UpdateResponse.latest` (et, si l'agent est
SIGNÉ, la signature Authenticode du MSI, du même éditeur), garde le MSI installé pour revenir en arrière,
lance `msiexec /i … /qn`, puis attend que le nouveau service se présente à la plateforme (5 minutes) ;
sinon il revient à la version d'avant. `agent_outdated` sur une route de travail : garder la liaison,
rappeler toutes les 5 minutes. `agent.json` gagne `"update": { "auto": true, "window": "03:00-05:00" }`.

Agent impact: étape 9 — aucun sur le fil ; un agent sert toutes les instances de son serveur (chaque
liaison nomme la sienne), un fichier de `pending\` peut décrire PLUSIEURS liaisons (`bindings`), et le
libellé services.msc se change (`AGENTLABEL`, `set-label`) — livré en 0.3.1. Étape 8 — rien de neuf sur le fil ; l'agent 0.3.0 implémente l'authentification
Windows (`sql.auth: windows`, vérification faite PAR LE SERVICE sous son identité, via `pending\`).
Étape 6 — `add-binding` accepte une adresse `http://` vers N'IMPORTE QUELLE machine —
plus seulement `localhost` — et avertit qu'en HTTP le jeton et le plan comptable circulent en clair ;
l'agent appelle toujours `PairResponse.endpoint`, que la plateforme renvoie désormais juste (l'adresse
appelée) ; un agent appairé avant ce correctif depuis une autre machine garde `localhost` dans son
fichier de liaison et doit être ré-appairé. Étape 7 — la vérification du compte SQL ne refuse plus
qu'un compte qui ne LIT pas `F_COMPTEG` : un compte qui peut écrire, ou qui voit toutes les bases
(`sa` compris), est accepté et l'agent le DIT ; son code, lui, ne fait toujours que lire.

Agent impact of steps 1–5 (delivered in Phase 5): la phase 5 implémente ce contrat : un DTO C# par message
de `schema/agent-protocol.schema.json`, un test qui désérialise et re-sérialise chaque fixture, les
en-têtes `X-Agent-Protocol` / `X-Agent-Version` (forme `1.2.3`, 64 caractères au plus), l'adresse
`endpoint` reçue à l'appairage et gardée avec la liaison pour tous les appels suivants, le `jobId`
dans le CORPS de `complete` et `fail`, et `sync_gl_accounts` qui lit `CG_Num`, `CG_Intitule` (rogné,
`null` si vide), `CG_Sommeil`, `CG_Type` — sans rien filtrer : c'est la plateforme qui décide. Le
compte SQL d'une liaison est privé de `VIEW ANY DATABASE`.
