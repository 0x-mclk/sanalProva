-- Tam yedeği YENİ bir veritabanına geri yükler (Pilot Faz A). Mevcut hiçbir veritabanının üzerine YAZMAZ (REPLACE yok): hedef zaten varsa durur.
--   sqlcmd -S sqlserver,1433 -U sa -C -b -v SourceDatabase=sanalprova TargetDatabase=sanalprova_restore_20261101 BackupFile=... Recovery=RECOVERY -i restore.sql
--   Recovery=NORECOVERY: ardından günlük yedekleri restore-log.sql ile uygulanacaksa.
-- Mantıksal dosya adları, kaynak veritabanı CREATE DATABASE ile açıldığı için "<SourceDatabase>" ve "<SourceDatabase>_log" biçimindedir (provision-production.sql böyle açar).
-- Canlıya geçiş (geri yüklenen veritabanını üretim adına almak) BU BETİKLE YAPILMAZ: docs/19 §geri yükleme, elle ve bilinçli adımdır.
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
IF N'$(TargetDatabase)' IN (N'sanalprova_dev', N'sanalprova_test', N'sanalprova_e2e', N'master', N'tempdb', N'model', N'msdb')
    THROW 51333, N'Hedef veritabanı korumalı bir ad (geliştirme/test/sistem).', 1;
IF DB_ID(N'$(TargetDatabase)') IS NOT NULL
    THROW 51334, N'Hedef veritabanı zaten var: üzerine yazılmaz. Yeni bir hedef adı verin.', 1;
IF N'$(Recovery)' NOT IN (N'RECOVERY', N'NORECOVERY')
    THROW 51335, N'Recovery değeri RECOVERY veya NORECOVERY olmalı.', 1;
GO
DECLARE @data nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000));
DECLARE @log nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000));
DECLARE @mdf nvarchar(4000) = @data + N'$(TargetDatabase).mdf';
DECLARE @ldf nvarchar(4000) = @log + N'$(TargetDatabase)_log.ldf';
IF N'$(Recovery)' = N'RECOVERY'
    RESTORE DATABASE [$(TargetDatabase)] FROM DISK = N'$(BackupFile)'
        WITH CHECKSUM, MOVE N'$(SourceDatabase)' TO @mdf, MOVE N'$(SourceDatabase)_log' TO @ldf, RECOVERY, STATS = 25;
ELSE
    RESTORE DATABASE [$(TargetDatabase)] FROM DISK = N'$(BackupFile)'
        WITH CHECKSUM, MOVE N'$(SourceDatabase)' TO @mdf, MOVE N'$(SourceDatabase)_log' TO @ldf, NORECOVERY, STATS = 25;
GO
PRINT N'Geri yükleme tamamlandı (hedef: yeni veritabanı). Sıradaki: post-restore-quarantine.sql, post-restore-check.sql.';
GO
