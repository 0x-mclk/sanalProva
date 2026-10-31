-- Sanal Prova ÜRETİM sertleştirmesi: uygulama hesabını (sanalprova_app) EN AZ YETKİYE indirir (Pilot Faz A). Her migration'dan SONRA çalıştırılır; idempotenttir.
--   sqlcmd -S sqlserver,1433 -U sa -C -b -v Database=sanalprova -i harden-production.sql
--   (yalnızca atılabilir lab veritabanı testi için: -v AllowLoginlessUsers=1  → login yoksa WITHOUT LOGIN kullanıcı oluşturur; üretimde 0 olmalı)
--
-- İLKE: uygulama yalnızca VERİ okur/yazar. Şema değiştiremez, tabloları oluşturamaz/silemez, tetikleyicileri kapatamaz, yedek alamaz.
--  - db_datareader/db_datawriter üyeliği KALDIRILIR; yerine dbo şeması üzerinde AÇIK SELECT/INSERT/UPDATE/DELETE (+ EXECUTE) verilir.
--  - DENY ALTER (şema), tablo/görünüm/prosedür/işlev/şema oluşturma, BACKUP, ALTER ANY DATABASE DDL TRIGGER... (DENY, rol üyeliğini de ezer).
--    (VIEW DEFINITION bilerek reddedilmez: meta veri görünürlüğünü kısmak yetki sorgularını (HAS_PERMS_BY_NAME) bozar ve güvenlik kazancı yoktur.)
--  - YALNIZCA EKLEME (append-only) defterleri: UPDATE/DELETE reddedilir. Yeni bir append-only tablo eklendiğinde bu betiğin listesine EKLENMELİDİR
--    (bir test, INSTEAD OF tetikleyicili her tablonun bu listede olduğunu doğrular).
--  - Kredi defteri (credit_ledger) ayrıca INSTEAD OF UPDATE/DELETE tetikleyicisiyle korunur (migration); uygulama tetikleyiciyi kapatamaz (ALTER yetkisi yok).
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
USE [$(Database)];
GO

IF N'$(Database)' IN (N'sanalprova_dev', N'sanalprova_test', N'sanalprova_e2e', N'master', N'tempdb', N'model', N'msdb')
    THROW 51310, N'Üretim sertleştirmesi geliştirme/test/sistem veritabanında çalıştırılmaz.', 1;
GO

IF USER_ID(N'sanalprova_app') IS NULL
BEGIN
    IF SUSER_ID(N'sanalprova_app') IS NOT NULL
        CREATE USER [sanalprova_app] FOR LOGIN [sanalprova_app] WITH DEFAULT_SCHEMA = [dbo];
    ELSE IF N'$(AllowLoginlessUsers)' = N'1'
        CREATE USER [sanalprova_app] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo];
    ELSE
        THROW 51311, N'sanalprova_app login/kullanıcısı yok: önce provision-production.sql çalıştırın.', 1;
END
GO

-- 1) Geniş rol üyelikleri kaldırılır (varsa)
IF IS_ROLEMEMBER(N'db_datareader', N'sanalprova_app') = 1 ALTER ROLE [db_datareader] DROP MEMBER [sanalprova_app];
IF IS_ROLEMEMBER(N'db_datawriter', N'sanalprova_app') = 1 ALTER ROLE [db_datawriter] DROP MEMBER [sanalprova_app];
IF IS_ROLEMEMBER(N'db_ddladmin', N'sanalprova_app') = 1 ALTER ROLE [db_ddladmin] DROP MEMBER [sanalprova_app];
IF IS_ROLEMEMBER(N'db_owner', N'sanalprova_app') = 1 ALTER ROLE [db_owner] DROP MEMBER [sanalprova_app];
IF IS_ROLEMEMBER(N'db_backupoperator', N'sanalprova_app') = 1 ALTER ROLE [db_backupoperator] DROP MEMBER [sanalprova_app];
GO

-- 2) Açık veri izinleri (yalnızca dbo şeması; sistem nesneleri hariç)
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [sanalprova_app];
GRANT EXECUTE ON SCHEMA::dbo TO [sanalprova_app];
GO

-- 3) Şema/yedek/DDL reddi (DENY, geçmişte verilmiş izinleri de ezer)
DENY ALTER, REFERENCES ON SCHEMA::dbo TO [sanalprova_app]; -- CONTROL/TAKE OWNERSHIP BİLEREK REDDEDİLMEZ: DENY CONTROL, şemadaki SELECT/INSERT dahil tüm ima edilen izinleri de reddeder
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA, CREATE TYPE, CREATE RULE, CREATE DEFAULT TO [sanalprova_app];
DENY BACKUP DATABASE, BACKUP LOG, ALTER ANY SCHEMA, ALTER ANY USER, ALTER ANY ROLE, ALTER ANY DATABASE DDL TRIGGER TO [sanalprova_app];
GO

-- 4) Yalnızca ekleme (append-only) tablolar: tablo varsa UPDATE/DELETE reddedilir (yoksa atlanır; migration sonrası yeniden çalıştırın)
DECLARE @appendOnly TABLE (name sysname PRIMARY KEY, denyInsert bit NOT NULL);
INSERT @appendOnly VALUES
    (N'__ef_migrations_history', 1),          -- migration geçmişi: yalnızca migrator yazar (INSERT dahil reddedilir)
    (N'ai_cost_reconciliations', 0),          -- mutabakat kararları
    (N'organization_agreement_acceptances', 0), -- sözleşme kabulleri
    (N'credit_ledger', 0),                    -- kredi defteri (ayrıca tetikleyici)
    (N'audit_logs', 0);                       -- denetim kayıtları
DECLARE @t sysname, @denyInsert bit, @sql nvarchar(max);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT name, denyInsert FROM @appendOnly;
OPEN cur;
FETCH NEXT FROM cur INTO @t, @denyInsert;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @t, N'U') IS NOT NULL
    BEGIN
        SET @sql = N'DENY UPDATE, DELETE' + CASE WHEN @denyInsert = 1 THEN N', INSERT' ELSE N'' END + N' ON OBJECT::dbo.' + QUOTENAME(@t) + N' TO [sanalprova_app];';
        EXEC sys.sp_executesql @sql;
    END
    ELSE PRINT N'(atlandı: dbo.' + @t + N' henüz yok)';
    FETCH NEXT FROM cur INTO @t, @denyInsert;
END
CLOSE cur;
DEALLOCATE cur;
GO

-- 5) Yedek hesabı: yalnızca yedek rolü (veri erişimi yok)
IF USER_ID(N'sanalprova_backup') IS NOT NULL
BEGIN
    IF IS_ROLEMEMBER(N'db_datareader', N'sanalprova_backup') = 1 ALTER ROLE [db_datareader] DROP MEMBER [sanalprova_backup];
    IF IS_ROLEMEMBER(N'db_datawriter', N'sanalprova_backup') = 1 ALTER ROLE [db_datawriter] DROP MEMBER [sanalprova_backup];
    IF IS_ROLEMEMBER(N'db_backupoperator', N'sanalprova_backup') = 0 ALTER ROLE [db_backupoperator] ADD MEMBER [sanalprova_backup];
END
GO
PRINT N'sanalprova_app: yalnızca veri okuma/yazma; şema/yedek yetkisi yok; defterler yalnızca eklemeye açık.';
GO
