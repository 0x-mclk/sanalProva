-- "Yalnizca ekleme" (append-only) tablolarin sertlestirmesi (ASCII).
--
-- NEDEN: ai_cost_reconciliations muhasebe kararlarinin degismez defteridir. Uygulama bu tabloya yalnizca INSERT yapar ve okur
-- (kod taramasiyla dogrulandi); ama sanalprova_app db_datawriter uyesi oldugu icin ele gecirilen/hatali kod kararlari guncelleyip silebilirdi.
-- NE YAPAR: yalnizca sanalprova_app icin UPDATE ve DELETE reddedilir. INSERT, SELECT, migrator ve diger tablolar etkilenmez.
-- NOT: oturum silme (KVKK) bu tabloya dokunmaz (kararlar muhasebe kaydidir, kisisel veri icermez).
-- Idempotent; tablo yoksa atlanir (dagitim sirasi: provision -> migrate -> harden).
-- KULLANIM:  sqlcmd -S .\SANALPROVA -E -C -I -b -v Database="sanalprova_test" -i scripts/mssql/harden-append-only.sql
--            (mevcut dev/test icin: scripts/apply-history-hardening.ps1 -Hardening AppendOnly, varsayilani kuru calismadir)
SET NOCOUNT ON;
SET XACT_ABORT ON;
USE [$(Database)];
GO
IF OBJECT_ID(N'dbo.ai_cost_reconciliations', N'U') IS NULL
    PRINT N'dbo.ai_cost_reconciliations henuz yok: atlandi. Migration uygulandiktan sonra bu betigi yeniden calistirin.';
ELSE IF USER_ID(N'sanalprova_app') IS NULL
    THROW 51011, N'sanalprova_app kullanicisi bu veritabaninda yok.', 1;
ELSE
BEGIN
    DENY UPDATE, DELETE ON OBJECT::dbo.ai_cost_reconciliations TO [sanalprova_app];
    PRINT N'sanalprova_app: dbo.ai_cost_reconciliations uzerinde UPDATE/DELETE reddedildi.';
END
GO
