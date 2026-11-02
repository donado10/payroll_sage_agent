# `F_JOURNAUX` — Sage 100's journals, as measured

## Summary

`F_JOURNAUX` holds the journals of a Sage 100 company — achats, ventes, trésorerie, général, situation —
one row per journal, keyed by `JO_Num`. This page is its shape **as measured on 2026-10-29** on
`PRESTIGE_TEXTILE.bak` (the same restore as `f-compteg.md`: SQL Server 2019, `French_CI_AS`), in
`test/sage/`. It records structure and aggregates only — **never a journal of the client**. What the
platform does with it is the contract's: `platform/protocol.md` › « `sync_journals` ».

## Columns

27 columns. Read from `INFORMATION_SCHEMA.COLUMNS` as `agent_a` (the binding's read-only login, which
reads the table: `HAS_PERMS_BY_NAME(…, 'SELECT')` = 1). Only those that bear on the sync, or might:

| # | Column | Type | Null | Notes |
| --- | --- | --- | --- | --- |
| 1 | `JO_Num` | `varchar(7)` | NO | the journal's code; unique (`IJO_NUM`) — **read**, sent as `code` |
| 3 | `JO_Intitule` | `varchar(35)` | yes | the label — **read** |
| 5 | `CG_Num` | `varchar(13)` | yes | the journal's compte général (`FKIA_F_JOURNAUX_CG_Num` → `F_COMPTEG`) — **read**, sent as `account` |
| 7 | `JO_Type` | `smallint` | **yes** | the journal's type — **read**, sent as `sageType` (`null` when NULL) |
| 8 | `JO_NumPiece` | `smallint` | yes | how pièces are numbered |
| 9 | `JO_Contrepartie` | `smallint` | yes | |
| 10 | `JO_SaisAnal` | `smallint` | yes | |
| 12 | `JO_Rappro` | `smallint` | yes | |
| 13 | `JO_Sommeil` | `smallint` | **yes** | « en sommeil » flag — **read**, sent as `dormant` (`false` when NULL) |
| 15 | `JO_Reglement` | `smallint` | yes | |
| 16 | `JO_SuiviTreso` | `smallint` | yes | |
| 21 | `cbMarq` | `int` | NO | Sage's row id — the primary key (`PK_CBMARQ_F_JOURNAUX`) |
| 23 | `cbModification` | `datetime` | yes | last modification |

Every `cb…` text column (`cbJO_Num`, `cbJO_Intitule`, `cbCG_Num`) is Sage's sort key of its neighbour;
never read. Do not hard-code a meaning for any other column from memory; the contract decides what is sent.

## What the measured company holds

| Fact | Measured |
| --- | --- |
| Rows | 5 |
| `JO_Type` | 1 × `0`, 1 × `1`, 1 × `2`, 2 × `3`; no NULL, no `4` |
| `CG_Num` | set on the type-`2` journal only, and that account exists in `F_COMPTEG`; never padded |
| `JO_Sommeil` | 5 × `0`; no NULL |
| `JO_Num` | 3 characters at most, `[0-9A-Z]` only, never padded |
| `JO_Intitule` | none NULL, empty or padded; 19 characters at most |

By Sage's own convention `JO_Type` is 0 achats, 1 ventes, 2 trésorerie, 3 général, 4 situation. What the
measurement confirms is the one that matters: only the trésorerie journal carries a `CG_Num` — its bank or
caisse account. The agent sends every type; the platform keeps 2 (as a treasury journal, with that account)
and 3 (as an OD journal) and skips the rest.

## Reading it correctly

- **The same rules as `F_COMPTEG`**: text is code page 1252, read as .NET strings; **trim** the code, the
  label and `CG_Num`; key on `JO_Num`.
- **A filter matches the TRIMMED `JO_Num`** (`LTRIM(RTRIM(JO_Num)) LIKE @p…`, 0.8.0), for the reason
  `f-compteg.md` gives; `French_CI_AS` makes it case-insensitive, as the platform's `upper()` does — and
  makes two codes that differ only by case impossible, which the agent checks anyway.
- **`CG_Num` is a link, not a condition**: the platform links the treasury journal to the compte général of
  that ERP with this number when it has it, and takes the journal without one otherwise
  (platform/protocol.md › « sync_journals »). The account matters only when a treasury écriture is passed.
