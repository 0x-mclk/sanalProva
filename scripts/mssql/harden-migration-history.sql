-- Migration gecmisi sertlestirmesi (ASCII; sqlcmd -f 65001 gerekmez).
--
-- NEDEN: sanalprova_app db_datawriter uyesidir ve bu rol HER tabloya yazma verir; ele gecirilen uygulama hesabi
-- __ef_migrations_history satirlarini degistirip migrator'u yaniltabilirdi (sahte "uygulandi" kaydi). DENY, rol uyeligini ezer.
-- NE YAPAR: yalnizca sanalprova_app icin dbo.__ef_migrations_history uzerinde INSERT/UPDATE/DELETE reddedilir. SELECT, migrator ve
-- diger tablolar etkilenmez. DENY idempotenttir (tekrar calistirmak zararsiz).
-- SIRA: tablo migration ile olusur. Tablo henuz yoksa betik hicbir sey yapmadan atlar; migration SONRASI yeniden calistirin
-- (dagitim sirasi: provision -> migrate -> harden).
-- KULLANIM:  sqlcmd -S .\SANALPROVA -E -C -I -b -v Database="sanalprova_test" -i scripts/mssql/harden-migration-history.sql
--            (mevcut dev/test icin onerilen yol: scripts/apply-history-hardening.ps1, varsayilani kuru calismadir)
SET NOCOUNT ON;
SET XACT_ABORT ON;
USE [$(Database)];
GO
IF OBJECT_ID(N'dbo.__ef_migrations_history', N'U') IS NULL
    PRINT N'dbo.__ef_migrations_history henuz yok: atlandi. Migration uygulandiktan sonra bu betigi yeniden calistirin.';
ELSE IF USER_ID(N'sanalprova_app') IS NULL
    THROW 51010, N'sanalprova_app kullanicisi bu veritabaninda yok.', 1;
ELSE
BEGIN
    DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.__ef_migrations_history TO [sanalprova_app];
    PRINT N'sanalprova_app: dbo.__ef_migrations_history uzerinde INSERT/UPDATE/DELETE reddedildi.';
END
GO
