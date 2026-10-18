-- One binding's SQL login: READ-ONLY, on ONE database, and blind to the rest of the instance.
-- This is the shape .claude/rules/sage-sql-access.md asks of a customer's server too.
-- Variables (from the environment): $(Login)  $(Password)  $(Database)
SET NOCOUNT ON;

IF SUSER_ID(N'$(Login)') IS NULL
    CREATE LOGIN [$(Login)] WITH PASSWORD = N'$(Password)', DEFAULT_DATABASE = [$(Database)],
        CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
ELSE
    ALTER LOGIN [$(Login)] WITH PASSWORD = N'$(Password)', DEFAULT_DATABASE = [$(Database)];

-- [HARD] Without this, `public` holds VIEW ANY DATABASE and the login can list EVERY database of
-- the instance — every other client of a hosting company, by name.
DENY VIEW ANY DATABASE TO [$(Login)];
GO

USE [$(Database)];
IF DATABASE_PRINCIPAL_ID(N'$(Login)') IS NULL
    CREATE USER [$(Login)] FOR LOGIN [$(Login)];
ALTER ROLE db_datareader ADD MEMBER [$(Login)];
PRINT N'$(Login) : lecture seule sur $(Database).';
