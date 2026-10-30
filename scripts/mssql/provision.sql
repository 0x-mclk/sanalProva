-- (Takipli kopya: scripts/mssql/provision.sql. Ilk kurulum kaydi .local/backups/mssql-install-20261026/ altindadir ve DEGISTIRILMEDI.)
-- Sanal Prova: SANALPROVA örneğinde geliştirme/test veritabanları ve en az yetkili kullanıcılar.
-- Parolalar BU DOSYADA YOKTUR: sqlcmd ortam değişkenlerinden okur (SP_MIGRATOR_PW, SP_APP_PW).
-- Idempotent: yeniden çalıştırmak mevcut veritabanlarını/login'leri silmez veya parolasını değiştirmez.
SET NOCOUNT ON;
GO

-- 1) Veritabanları (ayrı dev / test), collation sabit
IF DB_ID(N'sanalprova_dev') IS NULL CREATE DATABASE [sanalprova_dev] COLLATE Latin1_General_100_CI_AS_SC;
IF DB_ID(N'sanalprova_test') IS NULL CREATE DATABASE [sanalprova_test] COLLATE Latin1_General_100_CI_AS_SC;
GO

-- 2) Veritabanı ayarları: uyumluluk 160 (EF kararı), READ_COMMITTED_SNAPSHOT, SIMPLE kurtarma (yalnızca dev/test), sahip: devre dışı sa
DECLARE @db sysname, @sql nvarchar(max);
DECLARE dbs CURSOR LOCAL FAST_FORWARD FOR SELECT N'sanalprova_dev' UNION ALL SELECT N'sanalprova_test';
OPEN dbs;
FETCH NEXT FROM dbs INTO @db;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET COMPATIBILITY_LEVEL = 160; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET RECOVERY SIMPLE; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET PAGE_VERIFY CHECKSUM; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET AUTO_CLOSE OFF; '
             + N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET AUTO_SHRINK OFF; '
             + N'ALTER AUTHORIZATION ON DATABASE::' + QUOTENAME(@db) + N' TO [sa];';
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM dbs INTO @db;
END
CLOSE dbs;
DEALLOCATE dbs;
GO

-- 3) Sunucu düzeyi sıkılaştırma (geri alınabilir; geri alma komutları rapordadır)
ALTER LOGIN [sa] DISABLE;
IF SUSER_ID(N'BUILTIN\Users') IS NOT NULL DROP LOGIN [BUILTIN\Users];
IF SUSER_ID(N'NT SERVICE\SQLTELEMETRY$SANALPROVA') IS NOT NULL DROP LOGIN [NT SERVICE\SQLTELEMETRY$SANALPROVA];
GO

-- 4) Uygulama login'leri (SQL kimlik doğrulaması; karma mod açılana kadar bağlanamazlar)
--    sysadmin / db_owner YOK. Parola ilkesi açık, süre dolumu kapalı (servis hesabı), dil us_english.
--    Login zaten varsa parolası bu çalıştırmadaki değerle güncellenir (user-secrets ile gerçek parola ayrışmasın).
IF SUSER_ID(N'sanalprova_migrator') IS NULL
    CREATE LOGIN [sanalprova_migrator] WITH PASSWORD = N'$(SP_MIGRATOR_PW)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master], DEFAULT_LANGUAGE = us_english;
ELSE
    ALTER LOGIN [sanalprova_migrator] WITH PASSWORD = N'$(SP_MIGRATOR_PW)';
IF SUSER_ID(N'sanalprova_app') IS NULL
    CREATE LOGIN [sanalprova_app] WITH PASSWORD = N'$(SP_APP_PW)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master], DEFAULT_LANGUAGE = us_english;
ELSE
    ALTER LOGIN [sanalprova_app] WITH PASSWORD = N'$(SP_APP_PW)';
GO

-- 5) sanalprova_dev: migrator = DDL + okuma/yazma (şema değiştirir, sunucu yetkisi yok); app = yalnızca okuma/yazma
USE [sanalprova_dev];
GO
IF USER_ID(N'sanalprova_migrator') IS NULL CREATE USER [sanalprova_migrator] FOR LOGIN [sanalprova_migrator] WITH DEFAULT_SCHEMA = [dbo];
IF USER_ID(N'sanalprova_app') IS NULL CREATE USER [sanalprova_app] FOR LOGIN [sanalprova_app] WITH DEFAULT_SCHEMA = [dbo];
ALTER ROLE [db_ddladmin] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datareader] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datareader] ADD MEMBER [sanalprova_app];
ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_app];
GO

-- 6) sanalprova_test: aynı roller (db_owner yok; test sıfırlaması şema düzeyinde yapılacak, DROP DATABASE gerekmez)
USE [sanalprova_test];
GO
IF USER_ID(N'sanalprova_migrator') IS NULL CREATE USER [sanalprova_migrator] FOR LOGIN [sanalprova_migrator] WITH DEFAULT_SCHEMA = [dbo];
IF USER_ID(N'sanalprova_app') IS NULL CREATE USER [sanalprova_app] FOR LOGIN [sanalprova_app] WITH DEFAULT_SCHEMA = [dbo];
ALTER ROLE [db_ddladmin] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datareader] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datareader] ADD MEMBER [sanalprova_app];
ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_app];
GO

-- 7) Migration gecmisi sertlestirmesi (SONRADAN calisir): tablo migration ile olusur. Tablo varsa sanalprova_app icin INSERT/UPDATE/DELETE
--    reddedilir, yoksa atlanir. Migration'dan sonra bu betigi yeniden calistirmak (veya harden-migration-history.sql) yeterlidir.
--    Ayni deyim scripts/mssql/harden-migration-history.sql icindedir (bir test ikisinin ayni oldugunu denetler).
-- (yalnizca ekleme tablolari: scripts/mssql/harden-append-only.sql ve scripts/mssql/harden-agreement-acceptances.sql ile ayni deyimler)
USE [sanalprova_dev];
GO
IF OBJECT_ID(N'dbo.__ef_migrations_history', N'U') IS NOT NULL AND USER_ID(N'sanalprova_app') IS NOT NULL
    DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.__ef_migrations_history TO [sanalprova_app];
IF OBJECT_ID(N'dbo.ai_cost_reconciliations', N'U') IS NOT NULL AND USER_ID(N'sanalprova_app') IS NOT NULL
    DENY UPDATE, DELETE ON OBJECT::dbo.ai_cost_reconciliations TO [sanalprova_app];
IF OBJECT_ID(N'dbo.organization_agreement_acceptances', N'U') IS NOT NULL AND USER_ID(N'sanalprova_app') IS NOT NULL
    DENY UPDATE, DELETE ON OBJECT::dbo.organization_agreement_acceptances TO [sanalprova_app];
GO
USE [sanalprova_test];
GO
IF OBJECT_ID(N'dbo.__ef_migrations_history', N'U') IS NOT NULL AND USER_ID(N'sanalprova_app') IS NOT NULL
    DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.__ef_migrations_history TO [sanalprova_app];
IF OBJECT_ID(N'dbo.ai_cost_reconciliations', N'U') IS NOT NULL AND USER_ID(N'sanalprova_app') IS NOT NULL
    DENY UPDATE, DELETE ON OBJECT::dbo.ai_cost_reconciliations TO [sanalprova_app];
IF OBJECT_ID(N'dbo.organization_agreement_acceptances', N'U') IS NOT NULL AND USER_ID(N'sanalprova_app') IS NOT NULL
    DENY UPDATE, DELETE ON OBJECT::dbo.organization_agreement_acceptances TO [sanalprova_app];
GO
