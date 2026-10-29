-- Sanal Prova: Docker/CI SQL Server konteyneri için veritabanları ve en az yetkili kullanıcılar.
-- scripts/mssql/provision.sql'in KONTEYNER karşılığıdır: Windows'a özgü sunucu sıkılaştırması (sa kapatma, BUILTIN\Users) YOKTUR
-- (konteynerde sa, db-provision/db-harden işleri için gerekir ve ağ dışına açılmaz).
-- Parolalar BU DOSYADA YOKTUR: sqlcmd değişkenleridir (SP_MIGRATOR_PW, SP_APP_PW), docker/db/run-sql.sh bunları ortamdan verir.
-- Idempotent: yeniden çalıştırmak veritabanlarını silmez; login parolaları bu çalıştırmadaki değere eşitlenir. Her ifade tekrar çalıştırılabilir
-- (IF DB_ID/SUSER_ID/USER_ID IS NULL, ALTER ... SET, ALTER LOGIN ... PASSWORD, IS_ROLEMEMBER korumalı ALTER ROLE): run-sql.sh, SQL Server Msg 1205
-- (deadlock kurbanı) alırsa betiği bu güvenceye dayanarak baştan çalıştırır.
SET NOCOUNT ON;
GO

IF DB_ID(N'sanalprova_dev') IS NULL CREATE DATABASE [sanalprova_dev] COLLATE Latin1_General_100_CI_AS_SC;
IF DB_ID(N'sanalprova_test') IS NULL CREATE DATABASE [sanalprova_test] COLLATE Latin1_General_100_CI_AS_SC;
IF DB_ID(N'sanalprova_e2e') IS NULL CREATE DATABASE [sanalprova_e2e] COLLATE Latin1_General_100_CI_AS_SC;
GO

DECLARE @db sysname, @sql nvarchar(max);
DECLARE dbs CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM (VALUES (N'sanalprova_dev'), (N'sanalprova_test'), (N'sanalprova_e2e')) AS v(name);
OPEN dbs;
FETCH NEXT FROM dbs INTO @db;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET COMPATIBILITY_LEVEL = 160; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET RECOVERY SIMPLE; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET PAGE_VERIFY CHECKSUM; '
             -- Express yeni veritabanlarini AUTO_CLOSE ON acar: bosta kapanir (her baglantida yeniden acilir, kapaliyken sys.databases.collation_name NULL).
             -- scripts/mssql/provision.sql ile esdegerlik (CI #6 bulgusu).
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET AUTO_CLOSE OFF; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET AUTO_SHRINK OFF;';
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM dbs INTO @db;
END
CLOSE dbs;
DEALLOCATE dbs;
GO

-- Uygulama girişleri: sysadmin / db_owner YOK
IF SUSER_ID(N'sanalprova_migrator') IS NULL
    CREATE LOGIN [sanalprova_migrator] WITH PASSWORD = N'$(SP_MIGRATOR_PW)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master], DEFAULT_LANGUAGE = us_english;
ELSE
    ALTER LOGIN [sanalprova_migrator] WITH PASSWORD = N'$(SP_MIGRATOR_PW)';
IF SUSER_ID(N'sanalprova_app') IS NULL
    CREATE LOGIN [sanalprova_app] WITH PASSWORD = N'$(SP_APP_PW)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master], DEFAULT_LANGUAGE = us_english;
ELSE
    ALTER LOGIN [sanalprova_app] WITH PASSWORD = N'$(SP_APP_PW)';
GO

-- Her veritabanında: migrator = DDL + okuma/yazma; app = yalnızca okuma/yazma
DECLARE @db2 sysname, @sql2 nvarchar(max);
DECLARE dbs2 CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM (VALUES (N'sanalprova_dev'), (N'sanalprova_test'), (N'sanalprova_e2e')) AS v(name);
OPEN dbs2;
FETCH NEXT FROM dbs2 INTO @db2;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql2 = N'USE ' + QUOTENAME(@db2) + N'; '
        + N'IF USER_ID(N''sanalprova_migrator'') IS NULL CREATE USER [sanalprova_migrator] FOR LOGIN [sanalprova_migrator] WITH DEFAULT_SCHEMA = [dbo]; '
        + N'IF USER_ID(N''sanalprova_app'') IS NULL CREATE USER [sanalprova_app] FOR LOGIN [sanalprova_app] WITH DEFAULT_SCHEMA = [dbo]; '
        + N'IF IS_ROLEMEMBER(N''db_ddladmin'', N''sanalprova_migrator'') = 0 ALTER ROLE [db_ddladmin] ADD MEMBER [sanalprova_migrator]; '
        + N'IF IS_ROLEMEMBER(N''db_datareader'', N''sanalprova_migrator'') = 0 ALTER ROLE [db_datareader] ADD MEMBER [sanalprova_migrator]; '
        + N'IF IS_ROLEMEMBER(N''db_datawriter'', N''sanalprova_migrator'') = 0 ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_migrator]; '
        + N'IF IS_ROLEMEMBER(N''db_datareader'', N''sanalprova_app'') = 0 ALTER ROLE [db_datareader] ADD MEMBER [sanalprova_app]; '
        + N'IF IS_ROLEMEMBER(N''db_datawriter'', N''sanalprova_app'') = 0 ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_app];';
    EXEC sys.sp_executesql @sql2;
    FETCH NEXT FROM dbs2 INTO @db2;
END
CLOSE dbs2;
DEALLOCATE dbs2;
GO
