-- L'accès d'une liaison en AUTHENTIFICATION WINDOWS : pas de mot de passe, c'est l'identité Windows du
-- SERVICE de l'agent qui se connecte. À lancer sur le serveur SQL (SSMS, menu Requête › Mode SQLCMD).
--
-- L'identité, selon où tourne SQL Server :
--   - sur LE MÊME serveur que l'agent :           NT SERVICE\PayrollSageAgent
--   - sur un AUTRE serveur, dans un domaine AD :  DOMAINE\NOM-DU-SERVEUR-DE-L-AGENT$   (le compte machine)
--   - sur un autre serveur, HORS domaine :        impossible — utilisez un compte SQL (compte-sql-lecture.sql)
--
-- Puis, sur le serveur de l'agent :
--   PayrollSageAgent.exe check-sql --sql-server … --database … --windows-auth

:setvar Login "NT SERVICE\PayrollSageAgent"
:setvar Database "PRESTIGE"

SET NOCOUNT ON;

IF SUSER_ID(N'$(Login)') IS NULL
    CREATE LOGIN [$(Login)] FROM WINDOWS WITH DEFAULT_DATABASE = [$(Database)];

-- Recommandé sur un serveur partagé entre plusieurs clients : sans ceci, l'identité peut lister toutes
-- les bases de l'instance. (Avec l'authentification Windows, TOUTES les liaisons de cet agent partagent
-- cette identité : chaque base reliée lui ouvre son accès.)
DENY VIEW ANY DATABASE TO [$(Login)];
GO

USE [$(Database)];
IF DATABASE_PRINCIPAL_ID(N'$(Login)') IS NULL
    CREATE USER [$(Login)] FOR LOGIN [$(Login)];
ALTER ROLE db_datareader ADD MEMBER [$(Login)];
PRINT N'$(Login) : lecture sur $(Database).';
