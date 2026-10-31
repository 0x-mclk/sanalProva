-- Geri yükleme sonrası kullanıcı-login eşlemesi (Pilot Faz A): YENİ/başka bir sunucuya geri yüklemede veritabanı kullanıcıları (sanalprova_app/migrator/backup) sunucudaki
-- aynı adlı login'lerin SID'iyle eşleşmez ("yetim kullanıcı"; giriş yapılamaz). Aynı sunucuda eşleşme zaten sağlamdır ve bu betik hiçbir şey değiştirmez. Idempotent.
-- Önce provision-production.sql ile login'lerin (yeni parolalarla) oluşturulmuş olması gerekir; login yoksa ilgili kullanıcı atlanır ve uyarı yazılır.
--   sqlcmd -S sqlserver,1433 -U sa -C -b -v Database=sanalprova_restore_20261101 -i post-restore-fix-users.sql
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
USE [$(Database)];
GO
IF N'$(Database)' IN (N'sanalprova_dev', N'sanalprova_test', N'sanalprova_e2e', N'master', N'tempdb', N'model', N'msdb')
    THROW 51341, N'Kullanıcı eşlemesi korumalı bir veritabanında çalıştırılmaz.', 1;
GO
DECLARE @name sysname, @sql nvarchar(max), @fixed int = 0;
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT dp.name FROM sys.database_principals dp
     WHERE dp.name IN (N'sanalprova_app', N'sanalprova_migrator', N'sanalprova_backup') AND dp.type = 'S'
       AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid);   -- SID'i sunucuda karşılığı olmayan = yetim
OPEN cur;
FETCH NEXT FROM cur INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF SUSER_ID(@name) IS NULL
        PRINT N'UYARI: sunucuda ' + @name + N' login''i yok; önce provision-production.sql çalıştırın (kullanıcı atlandı).';
    ELSE
    BEGIN
        SET @sql = N'ALTER USER ' + QUOTENAME(@name) + N' WITH LOGIN = ' + QUOTENAME(@name) + N';';
        EXEC sys.sp_executesql @sql;
        SET @fixed += 1;
    END
    FETCH NEXT FROM cur INTO @name;
END
CLOSE cur;
DEALLOCATE cur;
PRINT N'Yetim kullanıcı düzeltildi: ' + CAST(@fixed AS nvarchar(10));
GO
