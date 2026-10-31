-- Günlük yedeğini NORECOVERY durumundaki geri yükleme hedefine uygular (zaman noktasına geri dönüş). Son günlükte Recovery=RECOVERY verilir.
--   sqlcmd ... -v TargetDatabase=sanalprova_restore_20261101 BackupFile=...trn Recovery=NORECOVERY|RECOVERY StopAt="" -i restore-log.sql
--   StopAt: boş (tüm günlük) veya "2026-11-01T14:30:00" biçiminde zaman (yerel sunucu saati).
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
IF N'$(TargetDatabase)' IN (N'sanalprova_dev', N'sanalprova_test', N'sanalprova_e2e', N'master', N'tempdb', N'model', N'msdb')
    THROW 51333, N'Hedef veritabanı korumalı bir ad (geliştirme/test/sistem).', 1;
IF (SELECT state_desc FROM sys.databases WHERE name = N'$(TargetDatabase)') <> N'RESTORING'
    THROW 51336, N'Hedef RESTORING durumunda değil: önce restore.sql ile Recovery=NORECOVERY geri yükleme yapılmalı.', 1;
GO
DECLARE @stop datetime = TRY_CONVERT(datetime, NULLIF(N'$(StopAt)', N''), 126);
IF NULLIF(N'$(StopAt)', N'') IS NOT NULL AND @stop IS NULL THROW 51337, N'StopAt zaman biçimi geçersiz (örn. 2026-11-01T14:30:00).', 1;
IF N'$(Recovery)' = N'RECOVERY' AND @stop IS NULL RESTORE LOG [$(TargetDatabase)] FROM DISK = N'$(BackupFile)' WITH CHECKSUM, RECOVERY, STATS = 25;
ELSE IF N'$(Recovery)' = N'RECOVERY' RESTORE LOG [$(TargetDatabase)] FROM DISK = N'$(BackupFile)' WITH CHECKSUM, STOPAT = @stop, RECOVERY, STATS = 25;
ELSE IF @stop IS NULL RESTORE LOG [$(TargetDatabase)] FROM DISK = N'$(BackupFile)' WITH CHECKSUM, NORECOVERY, STATS = 25;
ELSE RESTORE LOG [$(TargetDatabase)] FROM DISK = N'$(BackupFile)' WITH CHECKSUM, STOPAT = @stop, NORECOVERY, STATS = 25;
GO
PRINT N'Günlük yedeği uygulandı.';
GO
