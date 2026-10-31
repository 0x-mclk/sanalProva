-- Sanal Prova ÜRETİM veritabanı kurulumu (Pilot Faz A). sa ile BİR KEZ (ve idempotent biçimde yeniden) çalıştırılır.
-- Parolalar BU DOSYADA YOKTUR: sqlcmd betik değişkenlerinden okunur (ortam değişkeni → -v). Betik hiçbir değeri yazdırmaz.
--   sqlcmd -S sqlserver,1433 -U sa -C -b -v Database=sanalprova SP_MIGRATOR_PW=... SP_APP_PW=... SP_BACKUP_PW=... -i provision-production.sql
-- NE YAPAR (hepsi idempotent):
--   1) $(Database) veritabanı (geliştirme/test/e2e/sistem adları REDDEDİLİR), collation sabit, RCSI açık, uyumluluk 160, PAGE_VERIFY CHECKSUM,
--      RECOVERY FULL (zaman noktasına geri dönüş için günlük yedekleri; yedek zinciri deploy/ops içindedir)
--   2) Üç ayrı login: migrator (şema: yalnızca DDL + okuma/yazma), app (uygulama: yalnızca veri; ayrıcalıklar harden-production.sql'de daraltılır), backup (yalnızca yedek)
--   3) sysadmin/db_owner üyeliği YOK (sa hariç); sa'nın parolası dağıtım sırrıdır, SQL portu yayımlanmaz
-- ÖNEMLİ: ALTER LOGIN ... PASSWORD mevcut login'in parolasını bu çalıştırmadaki değere çeker (sır döndürme için kasıtlı).
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF N'$(Database)' IN (N'sanalprova_dev', N'sanalprova_test', N'sanalprova_e2e', N'master', N'tempdb', N'model', N'msdb') OR N'$(Database)' LIKE N'sanalprova[_]lab[_]%'
    THROW 51300, N'Üretim veritabanı adı geliştirme/test/lab/sistem veritabanı olamaz.', 1;
GO

IF DB_ID(N'$(Database)') IS NULL
    CREATE DATABASE [$(Database)] COLLATE Latin1_General_100_CI_AS_SC;
GO

ALTER DATABASE [$(Database)] SET COMPATIBILITY_LEVEL = 160;
ALTER DATABASE [$(Database)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
ALTER DATABASE [$(Database)] SET RECOVERY FULL;
ALTER DATABASE [$(Database)] SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE [$(Database)] SET AUTO_CLOSE OFF;
ALTER DATABASE [$(Database)] SET AUTO_SHRINK OFF;
ALTER AUTHORIZATION ON DATABASE::[$(Database)] TO [sa];
GO

-- Login'ler: parola ilkesi açık, süre dolumu kapalı (servis hesabı), dil us_english, varsayılan veritabanı master DEĞİL kendi veritabanı değil: master (yetkisiz)
IF SUSER_ID(N'sanalprova_migrator') IS NULL
    CREATE LOGIN [sanalprova_migrator] WITH PASSWORD = N'$(SP_MIGRATOR_PW)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master], DEFAULT_LANGUAGE = us_english;
ELSE
    ALTER LOGIN [sanalprova_migrator] WITH PASSWORD = N'$(SP_MIGRATOR_PW)';
IF SUSER_ID(N'sanalprova_app') IS NULL
    CREATE LOGIN [sanalprova_app] WITH PASSWORD = N'$(SP_APP_PW)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master], DEFAULT_LANGUAGE = us_english;
ELSE
    ALTER LOGIN [sanalprova_app] WITH PASSWORD = N'$(SP_APP_PW)';
IF SUSER_ID(N'sanalprova_backup') IS NULL
    CREATE LOGIN [sanalprova_backup] WITH PASSWORD = N'$(SP_BACKUP_PW)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master], DEFAULT_LANGUAGE = us_english;
ELSE
    ALTER LOGIN [sanalprova_backup] WITH PASSWORD = N'$(SP_BACKUP_PW)';
GO

USE [$(Database)];
GO
IF USER_ID(N'sanalprova_migrator') IS NULL CREATE USER [sanalprova_migrator] FOR LOGIN [sanalprova_migrator] WITH DEFAULT_SCHEMA = [dbo];
IF USER_ID(N'sanalprova_app') IS NULL CREATE USER [sanalprova_app] FOR LOGIN [sanalprova_app] WITH DEFAULT_SCHEMA = [dbo];
IF USER_ID(N'sanalprova_backup') IS NULL CREATE USER [sanalprova_backup] FOR LOGIN [sanalprova_backup] WITH DEFAULT_SCHEMA = [dbo];
-- migrator: şemayı değiştirir (EF migration), veriyi okur/yazar (veri dönüştüren migration'lar); sunucu yetkisi yok
ALTER ROLE [db_ddladmin] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datareader] ADD MEMBER [sanalprova_migrator];
ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_migrator];
-- backup: yalnızca yedek (BACKUP DATABASE / BACKUP LOG); veri okuma/yazma yok
ALTER ROLE [db_backupoperator] ADD MEMBER [sanalprova_backup];
-- app: roller harden-production.sql'de AÇIK izinlere çevrilir (db_datareader/db_datawriter üyeliği kaldırılır)
GO
PRINT N'Üretim veritabanı hazır. Sıradaki adımlar: migration (migrator ile), sonra harden-production.sql.';
GO
