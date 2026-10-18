-- Un compte SQL pour UNE liaison de l'agent Sage d'IGF Paie : LECTURE SEULE, sur UNE base, et aveugle
-- au reste de l'instance (il ne peut même pas lister les autres bases).
--
-- FACULTATIF : l'agent accepte tout compte qui lit la base Sage, sa compris. Ce compte dédié est ce
-- qui est recommandé sur un serveur qui héberge plusieurs clients — il sépare leurs bases au niveau de
-- SQL Server lui-même. (La passation comptable, plus tard, demandera un compte qui peut écrire.)
--
-- Dans SQL Server Management Studio : menu Requête › Mode SQLCMD, renseignez les trois lignes
-- ci-dessous, puis Exécuter. En ligne de commande :
--   sqlcmd -S SERVEUR\INSTANCE -E -i compte-sql-lecture.sql -v Login="agent_prestige" Password="..." Database="PRESTIGE"
-- (dans ce cas, supprimez les trois lignes :setvar).
--
-- Un compte par base reliée : si une liaison fuit, elle n'ouvre qu'une base.

:setvar Login "agent_prestige"
:setvar Password "REMPLACER-PAR-UN-MOT-DE-PASSE-LONG"
:setvar Database "PRESTIGE"

SET NOCOUNT ON;

IF SUSER_ID(N'$(Login)') IS NULL
    CREATE LOGIN [$(Login)] WITH PASSWORD = N'$(Password)', DEFAULT_DATABASE = [$(Database)],
        CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
ELSE
BEGIN
    -- Un nouveau mot de passe lève « doit changer à la prochaine connexion » ; ALORS seulement
    -- l'expiration peut être coupée (SQL Server refuse de la couper tant que l'autre est posé). Sans
    -- cela, la synchronisation s'arrête le jour où le mot de passe expire.
    ALTER LOGIN [$(Login)] WITH PASSWORD = N'$(Password)', DEFAULT_DATABASE = [$(Database)];
    ALTER LOGIN [$(Login)] WITH CHECK_EXPIRATION = OFF;
END

-- Sans ceci, le rôle public permet de lister TOUTES les bases de l'instance — chez un hébergeur, les
-- noms de tous ses autres clients.
DENY VIEW ANY DATABASE TO [$(Login)];
GO

USE [$(Database)];
IF DATABASE_PRINCIPAL_ID(N'$(Login)') IS NULL
    CREATE USER [$(Login)] FOR LOGIN [$(Login)];
ALTER ROLE db_datareader ADD MEMBER [$(Login)];
PRINT N'$(Login) : lecture seule sur $(Database).';
