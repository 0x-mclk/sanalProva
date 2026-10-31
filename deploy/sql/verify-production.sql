-- Üretim yetki doğrulaması (Pilot Faz A): harden-production.sql sonrası çalıştırılır; beklenen izinlerden biri tutmuyorsa THROW (sqlcmd -b ile sıfırdan farklı çıkış).
--   sqlcmd -S sqlserver,1433 -U sa -C -b -v Database=sanalprova -i verify-production.sql
-- sanalprova_app kimliğine EXECUTE AS ile bürünür (sa gerekir) ve HAS_PERMS_BY_NAME ile etkin izinleri sorgular; hiçbir veri okunmaz/yazılmaz.
SET NOCOUNT ON;
GO
USE [$(Database)];
GO
IF USER_ID(N'sanalprova_app') IS NULL THROW 51320, N'sanalprova_app kullanıcısı yok.', 1;
GO
DECLARE @r TABLE (kontrol nvarchar(200), beklenen bit, gercek bit);
EXECUTE AS USER = N'sanalprova_app';
    -- Veri: izinli
    INSERT @r SELECT N'dbo.users SELECT', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.users', N'OBJECT', N'SELECT'), 0);
    INSERT @r SELECT N'dbo.users INSERT', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.users', N'OBJECT', N'INSERT'), 0);
    INSERT @r SELECT N'dbo.generation_jobs UPDATE', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.generation_jobs', N'OBJECT', N'UPDATE'), 0);
    INSERT @r SELECT N'dbo.tryon_sessions DELETE (KVKK silme)', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.tryon_sessions', N'OBJECT', N'DELETE'), 0);
    INSERT @r SELECT N'dbo.credit_accounts UPDATE', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.credit_accounts', N'OBJECT', N'UPDATE'), 0);
    INSERT @r SELECT N'dbo.credit_ledger INSERT', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.credit_ledger', N'OBJECT', N'INSERT'), 0);
    INSERT @r SELECT N'dbo.credit_ledger SELECT', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.credit_ledger', N'OBJECT', N'SELECT'), 0);
    INSERT @r SELECT N'dbo.audit_logs INSERT', 1, ISNULL(HAS_PERMS_BY_NAME(N'dbo.audit_logs', N'OBJECT', N'INSERT'), 0);
    -- Yalnızca ekleme: UPDATE/DELETE reddedilmiş
    INSERT @r SELECT N'dbo.credit_ledger UPDATE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.credit_ledger', N'OBJECT', N'UPDATE'), 0);
    INSERT @r SELECT N'dbo.credit_ledger DELETE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.credit_ledger', N'OBJECT', N'DELETE'), 0);
    INSERT @r SELECT N'dbo.ai_cost_reconciliations UPDATE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.ai_cost_reconciliations', N'OBJECT', N'UPDATE'), 0);
    INSERT @r SELECT N'dbo.ai_cost_reconciliations DELETE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.ai_cost_reconciliations', N'OBJECT', N'DELETE'), 0);
    INSERT @r SELECT N'dbo.organization_agreement_acceptances UPDATE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.organization_agreement_acceptances', N'OBJECT', N'UPDATE'), 0);
    INSERT @r SELECT N'dbo.organization_agreement_acceptances DELETE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.organization_agreement_acceptances', N'OBJECT', N'DELETE'), 0);
    INSERT @r SELECT N'dbo.audit_logs UPDATE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.audit_logs', N'OBJECT', N'UPDATE'), 0);
    INSERT @r SELECT N'dbo.audit_logs DELETE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.audit_logs', N'OBJECT', N'DELETE'), 0);
    INSERT @r SELECT N'dbo.__ef_migrations_history INSERT', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.__ef_migrations_history', N'OBJECT', N'INSERT'), 0);
    INSERT @r SELECT N'dbo.__ef_migrations_history UPDATE', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.__ef_migrations_history', N'OBJECT', N'UPDATE'), 0);
    -- Şema/DDL/yedek: yok
    INSERT @r SELECT N'ALTER ANY SCHEMA', 0, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY SCHEMA'), 0);
    INSERT @r SELECT N'CREATE TABLE', 0, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0);
    INSERT @r SELECT N'BACKUP DATABASE', 0, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'BACKUP DATABASE'), 0);
    INSERT @r SELECT N'ALTER dbo.credit_ledger (tetikleyiciyi kapatma)', 0, ISNULL(HAS_PERMS_BY_NAME(N'dbo.credit_ledger', N'OBJECT', N'ALTER'), 0);
    INSERT @r SELECT N'CONTROL DATABASE', 0, ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL'), 0);
REVERT;
-- Roller: geniş üyelik yok
INSERT @r SELECT N'db_owner üyeliği', 0, CASE WHEN IS_ROLEMEMBER(N'db_owner', N'sanalprova_app') = 1 THEN 1 ELSE 0 END;
INSERT @r SELECT N'db_ddladmin üyeliği', 0, CASE WHEN IS_ROLEMEMBER(N'db_ddladmin', N'sanalprova_app') = 1 THEN 1 ELSE 0 END;
INSERT @r SELECT N'db_datawriter üyeliği (açık izinlerle değiştirilmiş olmalı)', 0, CASE WHEN IS_ROLEMEMBER(N'db_datawriter', N'sanalprova_app') = 1 THEN 1 ELSE 0 END;

SELECT kontrol, beklenen, gercek, CASE WHEN beklenen = gercek THEN N'TAMAM' ELSE N'HATA' END AS sonuc FROM @r ORDER BY sonuc DESC, kontrol;
IF EXISTS (SELECT 1 FROM @r WHERE beklenen <> gercek)
BEGIN
    DECLARE @msg nvarchar(2000) = N'Üretim yetki doğrulaması BAŞARISIZ (harden-production.sql yeniden çalıştırılmalı). Tutmayan: '
        + LEFT((SELECT STRING_AGG(kontrol + N' [beklenen ' + CAST(beklenen AS nvarchar(1)) + N', gerçek ' + CAST(gercek AS nvarchar(1)) + N']', N'; ') FROM @r WHERE beklenen <> gercek), 1800);
    THROW 51321, @msg, 1;
END
PRINT N'Üretim yetki doğrulaması TAMAM.';
GO
