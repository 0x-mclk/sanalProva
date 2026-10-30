-- "Sozlesme kabul kayitlari" sertlestirmesi (ASCII).
--
-- NEDEN: organization_agreement_acceptances, Owner'in hizmet sozlesmesi kabulunun degismez kaydidir (M5.1). Uygulama bu tabloya
-- yalnizca INSERT yapar ve okur; ama sanalprova_app db_datawriter uyesi oldugu icin ele gecirilen/hatali kod kabul kaydini
-- guncelleyip silebilirdi.
-- NE YAPAR: yalnizca sanalprova_app icin UPDATE ve DELETE reddedilir. INSERT, SELECT, migrator ve diger tablolar etkilenmez.
-- NOT: oturum silme (KVKK) bu tabloya dokunmaz (kabul kaydi firma/Owner'a aittir, musteri kisisel verisi degildir).
-- Idempotent; tablo yoksa atlanir (dagitim sirasi: provision -> migrate -> harden).
-- KULLANIM:  sqlcmd -S .\SANALPROVA -E -C -I -b -v Database="sanalprova_test" -i scripts/mssql/harden-agreement-acceptances.sql
--            (mevcut dev/test icin: scripts/apply-history-hardening.ps1 -Hardening AppendOnly, varsayilani kuru calismadir)
SET NOCOUNT ON;
SET XACT_ABORT ON;
USE [$(Database)];
GO
IF OBJECT_ID(N'dbo.organization_agreement_acceptances', N'U') IS NULL
    PRINT N'dbo.organization_agreement_acceptances henuz yok: atlandi. Migration uygulandiktan sonra bu betigi yeniden calistirin.';
ELSE IF USER_ID(N'sanalprova_app') IS NULL
    THROW 51012, N'sanalprova_app kullanicisi bu veritabaninda yok.', 1;
ELSE
BEGIN
    DENY UPDATE, DELETE ON OBJECT::dbo.organization_agreement_acceptances TO [sanalprova_app];
    PRINT N'sanalprova_app: dbo.organization_agreement_acceptances uzerinde UPDATE/DELETE reddedildi.';
END
GO
