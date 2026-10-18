-- Restore one .bak under one database name. Variables (from the environment, see setup.sh):
--   $(Database)  the name to restore under        $(Backup)  the .bak, as the container sees it
--   $(Replace)   1 = replace an existing database of that name, 0 = leave it alone
-- The file names come from the backup itself (RESTORE FILELISTONLY), so any Sage .bak restores.
-- A REPLACE first ends the sessions open on THAT database: a running agent keeps a pooled connection,
-- and SQL Server then refuses exclusive access — measured 2026-10-17, when this script still printed
-- « restaurée » over the error. Not `ALTER DATABASE … SET SINGLE_USER`: the two copies of one .bak
-- carry the same FILESTREAM directory name, and any ALTER DATABASE on the second fails on it
-- (Msg 33415), FILESTREAM unused or not. The script stops on the first error.
:on error exit
SET NOCOUNT ON;

IF DB_ID(N'$(Database)') IS NOT NULL AND N'$(Replace)' <> N'1'
BEGIN
    PRINT N'$(Database) existe déjà — laissée telle quelle (setup.sh --replace pour la remplacer).';
    RETURN;
END

DECLARE @files TABLE (
    LogicalName nvarchar(128), PhysicalName nvarchar(260), Type char(1), FileGroupName nvarchar(128),
    Size numeric(20, 0), MaxSize numeric(20, 0), FileId bigint, CreateLSN numeric(25, 0),
    DropLSN numeric(25, 0), UniqueId uniqueidentifier, ReadOnlyLSN numeric(25, 0),
    ReadWriteLSN numeric(25, 0), BackupSizeInBytes bigint, SourceBlockSize int, FileGroupId int,
    LogGroupGUID uniqueidentifier, DifferentialBaseLSN numeric(25, 0),
    DifferentialBaseGUID uniqueidentifier, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32),
    SnapshotUrl nvarchar(360));
INSERT INTO @files EXEC (N'RESTORE FILELISTONLY FROM DISK = N''$(Backup)''');

DECLARE @moves nvarchar(max) = (
    SELECT STRING_AGG(
        N'MOVE N''' + LogicalName + N''' TO N''/var/opt/mssql/data/$(Database)_' + CAST(FileId AS nvarchar(10))
        + CASE Type WHEN 'L' THEN N'.ldf' ELSE N'.mdf' END + N'''', N', ')
    FROM @files);

DECLARE @sql nvarchar(max) = N'RESTORE DATABASE [$(Database)] FROM DISK = N''$(Backup)'' WITH '
    + @moves + CASE WHEN N'$(Replace)' = N'1' THEN N', REPLACE' ELSE N'' END + N', RECOVERY, STATS = 50;';
DECLARE @kill nvarchar(max) = N'';
SELECT @kill += N'KILL ' + CAST(session_id AS nvarchar(10)) + N'; '
FROM sys.dm_exec_sessions
WHERE database_id = DB_ID(N'$(Database)') AND session_id <> @@SPID;
EXEC (@kill);
BEGIN TRY
    EXEC (@sql);
END TRY
BEGIN CATCH
    THROW;
END CATCH
PRINT N'$(Database) restaurée.';
