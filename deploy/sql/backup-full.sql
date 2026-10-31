-- Tam yedek (Pilot Faz A): sağlama toplamlı (SQL Server Express yedek sıkıştırmayı DESTEKLEMEZ: dosya düzeyinde gzip ops betiğinde), hemen doğrulanır. Yalnızca YEDEK alır; hiçbir veri değişmez.
--   sqlcmd -S sqlserver,1433 -U sanalprova_backup -C -b -v Database=sanalprova BackupFile=/var/opt/mssql/backup/sanalprova_full_YYYYMMDD_HHMMSS.bak -i backup-full.sql
-- Yedek dosyası mevcutsa ÜZERİNE YAZILMAZ (NOINIT + yeni dosya adı beklenir; var olan dosyaya ek set eklemek yerine ops betiği her seferinde benzersiz ad üretir).
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
IF DB_ID(N'$(Database)') IS NULL THROW 51330, N'Yedeklenecek veritabanı yok.', 1;
GO
BACKUP DATABASE [$(Database)] TO DISK = N'$(BackupFile)' WITH CHECKSUM, NOINIT, NAME = N'sanalprova-full', STATS = 25;
GO
RESTORE VERIFYONLY FROM DISK = N'$(BackupFile)' WITH CHECKSUM;
GO
PRINT N'Tam yedek alındı ve sağlama toplamıyla doğrulandı.';
GO
