# `F_COMPTEG` — Sage 100's chart of accounts, as measured

## Summary

`F_COMPTEG` holds the comptes généraux of a Sage 100 company: one row per account, keyed by
`CG_Num`. This page is its shape **as measured on 2026-10-17** on `PRESTIGE_TEXTILE.bak` (SQL Server
2019 build 15.0.4375, `French_CI_AS`, compatibility level 140, 1 348 accounts), restored in
`test/sage/`. It records structure and aggregates only — **never an account of the client**. What
the platform does with it is the contract's: `platform/protocol.md` › « `sync_gl_accounts` ».

## Columns

Read from `INFORMATION_SCHEMA.COLUMNS`, as `agent_a` (the binding's read-only login).

| # | Column | Type | Null | Notes |
| --- | --- | --- | --- | --- |
| 1 | `CG_Num` | `varchar(13)` | NO | the account number; unique (`UKA_F_COMPTEG_CG_Num`) |
| 2 | `cbCG_Num` | `varbinary(15)` | yes | Sage's sort key of `CG_Num` (unique index `ICG_NUM`) — not data |
| 3 | `CG_Type` | `smallint` | yes | account type code |
| 4 | `CG_Intitule` | `varchar(35)` | yes | the label |
| 5 | `CG_Classement` | `varchar(17)` | yes | sort label |
| 6 | `cbCG_Classement` | `varbinary(18)` | yes | sort key |
| 7 | `N_Nature` | `smallint` | yes | nature code (see below) |
| 8 | `CG_Report` | `smallint` | yes | |
| 9 | `CR_Num` | `varchar(13)` | yes | indexed (`FKIA_F_COMPTEG_CR_Num`) |
| 10 | `cbCR_Num` | `varbinary(14)` | yes | sort key |
| 11 | `CG_Raccourci` | `varchar(7)` | yes | shortcut |
| 12 | `cbCG_Raccourci` | `varbinary(8)` | yes | sort key |
| 13–21 | `CG_Saut`, `CG_Regroup`, `CG_Analytique`, `CG_Echeance`, `CG_Quantite`, `CG_Lettrage`, `CG_Tiers`, `CG_Devise`, `N_Devise` | `smallint` | yes | option flags and codes |
| 22 | `TA_Code` | `varchar(5)` | yes | indexed (`FKIA_F_COMPTEG_TA_Code`) |
| 23 | `cbTA_Code` | `varbinary(6)` | yes | sort key |
| 24 | `CG_Sommeil` | `smallint` | yes | « en sommeil » flag |
| 25–27 | `CG_ReportAnal`, `CG_LettrageSaisie`, `cbProt` | `smallint` | yes | |
| 28 | `cbMarq` | `int` | NO | Sage's row id — the primary key (`PK_CBMARQ_F_COMPTEG`) |
| 29 | `cbCreateur` | `char(4)` | yes | |
| 30 | `cbModification` | `datetime` | yes | last modification — NULL on no row here |
| 31–32 | `cbReplication`, `cbFlag` | `int`, `smallint` | yes | |
| 33 | `cbCreation` | `datetime` | yes | |
| 34 | `cbCreationUser` | `uniqueidentifier` | yes | |

The `cb*` columns are Sage's technical columns; the `cb<Col>` `varbinary` ones are its sort keys of
the text column of the same name.

## What the data says (aggregates)

| Fact | Measured |
| --- | --- |
| rows · distinct `CG_Num` | 1 348 · 1 348 |
| `CG_Num` length | 8 on every row; `[0-9A-Z]` only; no padding; no leading `0`; first digits 1–8 (SYSCOHADA classes) |
| `CG_Intitule` | never NULL or empty here; **243 with leading/trailing spaces**; 497 at the full 35 characters; 805 with accents |
| `CG_Type` | `0` on every row |
| `CG_Sommeil` | `0` on every row |
| `N_Nature` | 15 values, 0–14 |
| `CG_Tiers` | `1` on 33 rows |
| `CG_Analytique` | `1` on 399 rows |

**What is NOT known from this data**: the meaning of a `CG_Type` or `CG_Sommeil` other than `0`
(none occurs), and the names of the `N_Nature` codes — Sage does not store them: `P_NATURE` holds,
per nature, number ranges (`N_NatureNNN_Debut` / `_Fin`) and the account defaults, with no label.
Do not hard-code a meaning for any of them from memory; the contract decides what is sent.

## Reading it correctly

- **Text is code page 1252** (`French_CI_AS`, `varchar`). `Microsoft.Data.SqlClient` converts it to
  .NET strings; never read it as bytes.
- **Trim the label.** A quarter of the labels carry spaces Sage keeps; the platform's label is
  trimmed text.
- **Key on `CG_Num`**, as the platform does. Whether `cbMarq` survives a renumbering is not measured.
- **A filter matches the TRIMMED `CG_Num`** (`LTRIM(RTRIM(CG_Num)) LIKE @p…`, 0.6.0). No number was
  padded in the measured company, but SQL Server's `LIKE` counts a varchar's trailing spaces, so
  `%62` against a padded number would miss it — and the agent sends the trimmed number. `French_CI_AS`
  makes the match case-insensitive, as the platform's `upper()` does.
- The restore printed one warning: the database carries a FILESTREAM `DIRECTORY_NAME`
  (`PRESTIGEDECO`), which cannot be shared by the second copy on the same instance. Harmless for
  `F_COMPTEG`; a customer's server has one copy. **But any `ALTER DATABASE` on the second copy fails
  on it** (Msg 33415) — FILESTREAM unused, non-transacted access off — which is why
  `test/sage/sql/restore.sql` ends sessions rather than `SET SINGLE_USER`.

## Sage guards its own tables — measured 2026-10-17

`F_COMPTEG` carries six triggers, three Sage ones and three `CB` ones per operation
(`TG_INS_` / `TG_UPD_` / `TG_DEL_` and `TG_CBINS_` / `TG_CBUPD_` / `TG_CBDEL_`); the database holds
1 185 triggers, their definitions not readable as text. **An UPDATE from plain SQL is refused** on a
server where Sage is not installed: « Could not find stored procedure 'master..xp_CBIsFileLock' » and
the transaction ends in the trigger (Msg 3609). That procedure is part of a Sage installation; the
triggers use it to keep the database consistent with Sage's own locks.

- **For reading, nothing changes**: the agent only SELECTs, and no trigger fires on a read.
- **For the test copy**, Phase 6 edited `F_COMPTEG` with the two UPDATE triggers paused
  (`DISABLE TRIGGER … ENABLE TRIGGER`, as `sa`) and then restored the database from the backup. Never
  on a customer's server.
- **For the later WRITE slice** (écritures into Sage): do not plan on INSERT into Sage's tables from
  plain SQL. Even where `xp_CBIsFileLock` exists, writing beside Sage's triggers and locks is the path
  Sage does not support; the supported routes (Sage's own import format, its object model) are to be
  measured when that slice starts.
