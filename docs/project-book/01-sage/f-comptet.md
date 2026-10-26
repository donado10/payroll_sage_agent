# `F_COMPTET` — Sage 100's comptes tiers, as measured

## Summary

`F_COMPTET` holds the comptes tiers of a Sage 100 company — its clients, fournisseurs, salariés and
« autres » — one row per tiers, keyed by `CT_Num`. This page is its shape **as measured on 2026-10-26**
on `PRESTIGE_TEXTILE.bak` (the same restore as `f-compteg.md`: SQL Server 2019, `French_CI_AS`), in
`test/sage/`. It records structure and aggregates only — **never a tiers of the client**. What the
platform does with it is the contract's: `platform/protocol.md` › « `sync_partner_accounts` ».

## Columns

148 columns. Read from `INFORMATION_SCHEMA.COLUMNS` as `agent_a` (the binding's read-only login, which
reads the table: `HAS_PERMS_BY_NAME(…, 'SELECT')` = 1). Only those that bear on the sync, or might:

| # | Column | Type | Null | Notes |
| --- | --- | --- | --- | --- |
| 1 | `CT_Num` | `varchar(17)` | NO | the tiers' number; unique (`UKA_F_COMPTET_CT_Num`) — **read** |
| 3 | `CT_Intitule` | `varchar(69)` | yes | the label — **read** |
| 4 | `CT_Type` | `smallint` | **yes** | tiers type code — **read**, sent as `sageType` (`null` when NULL) |
| 5 | `CG_NumPrinc` | `varchar(13)` | yes | the tiers' collective compte général (`F_COMPTEG.CG_Num`) |
| 7 | `CT_Qualite` | `varchar(17)` | yes | |
| 8 | `CT_Classement` | `varchar(17)` | yes | sort label |
| 23 | `CT_Identifiant` | `varchar(25)` | yes | |
| 24 | `CT_Siret` | `varchar(15)` | yes | |
| 58 | `CT_Sommeil` | `smallint` | **yes** | « en sommeil » flag — **read**, sent as `dormant` (`false` when NULL) |
| 142 | `cbMarq` | `int` | NO | Sage's row id — the primary key (`PK_CBMARQ_F_COMPTET`) |
| 144 | `cbModification` | `datetime` | yes | last modification — set on every row here |

`ICT_NUM` is a unique index on Sage's sort key of `CT_Num`. **`CT_Type` and `CT_Sommeil` are NULLABLE
here**, where `F_COMPTEG`'s `CG_Type` reads 0 for a NULL: the contract's `sageType` is nullable for a
tiers, and a NULL `CT_Sommeil` is not dormant. Do not hard-code a meaning for any other column from
memory; the contract decides what is sent.

## What the measured company holds

| Fact | Measured |
| --- | --- |
| Rows | 145 |
| `CT_Type` | 136 × `0`, 9 × `1`; no NULL, no `2` or `3` |
| `CT_Sommeil` | 145 × `0`; no NULL |
| `CT_Num` | 8 characters on every row, `[0-9A-Z]` only, never padded; the `0` rows start with `C`, the `1` rows with `F` |
| `CT_Intitule` | none NULL or empty; 3 carry leading or trailing spaces; 31 characters at most; 1 holds a non-ASCII character (code page 1252) |
| `CG_NumPrinc` | one value per `CT_Type` |
| Triggers | 7 on the table — writing to it from SQL is Sage's business, as on `F_COMPTEG` |

By Sage's own convention `CT_Type` is 0 client, 1 fournisseur, 2 salarié, 3 autre — the measured
company has no salarié tiers, so the `2` is NOT measured. The agent sends every type; the platform takes
them all and the binding's `partnerFilter` chooses.

## Reading it correctly

- **The same rules as `F_COMPTEG`**: text is code page 1252, read as .NET strings; **trim the label**;
  key on `CT_Num`.
- **A filter matches the TRIMMED `CT_Num`** (`LTRIM(RTRIM(CT_Num)) LIKE @p…`, 0.7.0), for the reason
  `f-compteg.md` gives; `French_CI_AS` makes it case-insensitive, as the platform's `upper()` does.
- **The whole table can be large**: every CLIENT of the société is a tiers. More than 10 000 fails the
  job with a sentence that suggests a `partnerFilter`, rather than sending part of it.
