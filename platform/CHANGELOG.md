# Changelog of the Sage agent contract

Newest first. One entry per version of `version.json` › `protocol`, each with an « Agent impact »
line: what the agent repo must change, in plain words — the agent session reads exactly this.
`npm run check:agent-protocol -- --freeze` refuses a freeze whose top entry is not the current
version or has no « Agent impact ».

## v0 — 2026-10-26

The draft, written in steps — v0 is not frozen for anyone yet, so it was amended rather than
versioned:

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

12. **A filter on the accounts a binding syncs** (2026-10-26). The user asked for a list of `LIKE`
    patterns in `agent.json` — `["4%", "50%"]` — so that only those accounts come back, and decided that
    after the resync Compte général lists ONLY them. `SyncGlAccountsResult` gains an optional
    `accountFilter` (the patterns the agent READ with; `[0-9A-Za-z%_]{1,40}`, 50 at most), and the
    platform then deletes the accounts of that ERP outside it — except those a mapping uses, kept and
    named in the report (`keptInUse`); `removed` counts the others, and `absentFromSage` counts only what
    the filter covers. A filter that brought no account back is refused (`request_invalid`). Additive:
    an agent that sends no filter is exactly as before, and an older platform refuses the field — so a
    filter is used only once the platform has it.

13. **The comptes tiers from Sage** (2026-10-26). A second job type, `sync_partner_accounts`: the agent
    reads `F_COMPTET` (`CT_Num`, `CT_Intitule`, `CT_Sommeil`, `CT_Type` — the last two NULLABLE in Sage,
    so `sageType` may be `null`) and the platform applies the rules of the comptes généraux to the
    connection's comptes tiers, with two differences: every `sageType` is taken (client, fournisseur,
    salarié, autre — the filter chooses), and a tiers outside the filter is kept when a salarié is linked
    to it or a rubrique names it as its fixed tiers. The filter is `agent.json` › `partnerFilter`, sent
    back in the result's `accountFilter` (one result shape for both types). One open job per connection
    AND TYPE now, so both syncs can be asked together. `JOB_ERROR_LABELS` no longer say « plan
    comptable ». Additive: an agent that does not know the type fails it itself (`internal`, « mettez-le
    à jour »), and the platform hands it out only when a person asks for it on Compte tiers.

Both sides are built (Phases 4–6). v1 is declared before an agent is installed on a customer's server.

Agent impact: étape 13 — un second type de demande, `sync_partner_accounts` (charge utile `{}`). L'agent
lit `SELECT CT_Num, CT_Intitule, CT_Sommeil, CT_Type FROM dbo.F_COMPTET` (+ `WHERE LTRIM(RTRIM(CT_Num))
LIKE @p0 OR …` avec un filtre, `ORDER BY CT_Num`) et répond un `CompleteRequest` dont `result` a la forme de
`sync_gl_accounts` avec `type: "sync_partner_accounts"` : `number` = `CT_Num` rogné, `label` = `CT_Intitule`
rogné (`null` si vide ou NULL), `dormant` = `CT_Sommeil` ≠ 0 (**faux si NULL**), `sageType` = `CT_Type`
(**`null` si NULL** — les deux colonnes sont NULLABLES dans `F_COMPTET`). Le filtre est une seconde clé
d'`agent.json`, **`"partnerFilter"`**, en tout point comme `accountFilter` (partagée en haut et/ou par
liaison, la liaison l'emporte, `[]` = aucun, même grammaire, gardée avec la liaison, un changement de
filtre seul enregistré sans vérification SQL) ; ses motifs partent dans `result.accountFilter` — le même
champ que pour les comptes généraux —, la clé absente sans filtre. Aucun tiers ramené par le filtre :
`source_invalid`, non réessayable, une phrase qui cite `partnerFilter` ; plus de 10 000 tiers : la même
chose, la phrase conseille un `partnerFilter` (`F_COMPTET` contient aussi tous les CLIENTS). Un compte SQL
qui ne lit pas `F_COMPTET` : `sql_permission_denied` avec une phrase qui nomme les comptes tiers —
l'appairage ne vérifie toujours que `F_COMPTEG`. Le compte rendu (`CompleteResponse.report`) a la forme
de celui des comptes généraux. Une liaison peut désormais avoir DEUX demandes ouvertes (une par type) :
l'agent les prend une à une, comme aujourd'hui. Livré en 0.7.0 ; un agent plus ancien refuse la clé
`partnerFilter` dans `agent.json` et échoue la demande (« mettez-le à jour ») — il faut le mettre à jour
avant d'ajouter la clé.

Agent impact: étape 12 — `agent.json` accepte `"accountFilter": ["4%", "50%"]`, en haut du fichier
(partagé) et/ou sur chaque liaison (la liste de la liaison l'emporte ; `[]` = aucun filtre). Un motif
est le `LIKE` de SQL : lettres, chiffres, `%` et `_` (où qu'ils soient : `62%` commence par 62, `%62`
finit par 62, `%62%` contient 62 ; sans joker, un seul compte : `421100`), 40 caractères au plus, 50 motifs au plus — tout autre caractère est
refusé à la lecture du fichier, avec une phrase. Le filtre est gardé avec la liaison. `sync_gl_accounts`
lit alors `… FROM dbo.F_COMPTEG WHERE LTRIM(RTRIM(CG_Num)) LIKE @p0 OR LTRIM(RTRIM(CG_Num)) LIKE @p1 …`
(le numéro ROGNÉ, celui qu'il envoie : le `LIKE` de SQL Server compte les espaces de fin d'un varchar)
(des PARAMÈTRES, jamais le motif collé dans la requête) et envoie les motifs dans
`result.accountFilter` ; sans filtre, l'agent n'envoie PAS la clé. Si le filtre ne ramène aucun compte,
l'agent échoue la demande (`source_invalid`, non réessayable) avec une phrase qui cite le filtre, et
n'envoie rien. `CompleteResponse.report` gagne `accountFilter`, `removed` et `keptInUse` (à journaliser :
« N supprimés hors filtre, M gardés »). Livré en 0.6.0.

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
