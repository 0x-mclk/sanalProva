-- İşlem günlüğü yedeği (Pilot Faz A): FULL kurtarma modelinde zaman noktasına geri dönüş zincirinin parçası. Önce en az bir tam yedek alınmış olmalıdır.
--   sqlcmd -S sqlserver,1433 -U sanalprova_backup -C -b -v Database=sanalprova BackupFile=/var/opt/mssql/backup/sanalprova_log_YYYYMMDD_HHMMSS.trn -i backup-log.sql
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
IF DB_ID(N'$(Database)') IS NULL THROW 51330, N'Yedeklenecek veritabanı yok.', 1;
IF (SELECT recovery_model_desc FROM sys.databases WHERE name = N'$(Database)') <> N'FULL' THROW 51332, N'Günlük yedeği için kurtarma modeli FULL olmalı (provision-production.sql).', 1;
GO
BACKUP LOG [$(Database)] TO DISK = N'$(BackupFile)' WITH CHECKSUM, NOINIT, NAME = N'sanalprova-log', STATS = 25;
GO
RESTORE VERIFYONLY FROM DISK = N'$(BackupFile)' WITH CHECKSUM;
GO
PRINT N'Günlük yedeği alındı ve sağlama toplamıyla doğrulandı.';
GO
