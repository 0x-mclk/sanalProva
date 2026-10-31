-- Geri yükleme sonrası tutarlılık denetimi (Pilot Faz A): kredi defteri ile hesapların, maliyet kayıtlarının ve kısıtların tutarlılığı. Salt okunur.
--   sqlcmd -S sqlserver,1433 -U sa -C -b -v Database=sanalprova_restore_20261101 RunCheckDb=0 -i post-restore-check.sql
-- Herhangi bir denetim başarısızsa THROW (sqlcmd -b ile sıfırdan farklı çıkış): veritabanı üretime alınmamalıdır.
-- RunCheckDb=1: ayrıca DBCC CHECKDB (fiziksel/mantıksal bütünlük; büyük veritabanında uzun sürer).
SET NOCOUNT ON;
GO
USE [$(Database)];
GO
DECLARE @r TABLE (kontrol nvarchar(200), ihlal int);

-- Kredi: bakiye = Σ(Grant+Adjustment) − Σ(Consume); rezerve = Σ(Reserve) − Σ(Release) − Σ(Consume); negatif yok
INSERT @r
SELECT N'kredi bakiyesi = defter toplamı', COUNT(*)
  FROM credit_accounts a
 WHERE a.balance_credits <> ISNULL((SELECT SUM(l.amount) FROM credit_ledger l WHERE l.organization_id = a.organization_id AND l.entry_type IN (N'Grant', N'Adjustment')), 0)
                          - ISNULL((SELECT SUM(l.amount) FROM credit_ledger l WHERE l.organization_id = a.organization_id AND l.entry_type = N'Consume'), 0);
INSERT @r
SELECT N'kredi rezervesi = defter toplamı', COUNT(*)
  FROM credit_accounts a
 WHERE a.reserved_credits <> ISNULL((SELECT SUM(l.amount) FROM credit_ledger l WHERE l.organization_id = a.organization_id AND l.entry_type = N'Reserve'), 0)
                           - ISNULL((SELECT SUM(l.amount) FROM credit_ledger l WHERE l.organization_id = a.organization_id AND l.entry_type IN (N'Release', N'Consume')), 0);
INSERT @r SELECT N'hesabı olmayan defter girdisi', COUNT(*) FROM credit_ledger l WHERE NOT EXISTS (SELECT 1 FROM credit_accounts a WHERE a.organization_id = l.organization_id);
INSERT @r SELECT N'negatif/geçersiz kredi hesabı', COUNT(*) FROM credit_accounts WHERE balance_credits < 0 OR reserved_credits < 0 OR reserved_credits > balance_credits;
INSERT @r SELECT N'iş başına birden çok Reserve/kapanış', COUNT(*) FROM (SELECT job_id FROM credit_ledger WHERE job_id IS NOT NULL GROUP BY job_id
    HAVING SUM(CASE WHEN entry_type = N'Reserve' THEN 1 ELSE 0 END) > 1 OR SUM(CASE WHEN entry_type IN (N'Release', N'Consume') THEN 1 ELSE 0 END) > 1) x;
-- Maliyet: belirsiz kayıt olay anahtarları benzersiz, mutabakat kararı vakasıyla aynı firmada
INSERT @r SELECT N'mutabakat kararı vakadan farklı firmada', COUNT(*) FROM ai_cost_reconciliations d JOIN ai_usage_logs c ON c.id = d.usage_log_id WHERE d.organization_id <> c.organization_id;
-- Kısıtlar güvenilir (WITH CHECK ile doğrulanmış)
INSERT @r SELECT N'güvenilmeyen CHECK kısıtı', COUNT(*) FROM sys.check_constraints WHERE is_not_trusted = 1 AND is_disabled = 0;
INSERT @r SELECT N'güvenilmeyen yabancı anahtar', COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1 AND is_disabled = 0;
-- Karantina yapıldı mı: çalışmaya hazır iş kalmamalı
INSERT @r SELECT N'karantina sonrası Queued/Running iş (post-restore-quarantine.sql çalıştırılmamış)', COUNT(*) FROM generation_jobs WHERE status IN (N'Queued', N'Running');
INSERT @r SELECT N'karantina sonrası açık kurulum bağlantısı', COUNT(*) FROM account_setup_tokens WHERE used_at IS NULL AND revoked_at IS NULL AND expires_at > SYSDATETIMEOFFSET();

SELECT kontrol, ihlal, CASE WHEN ihlal = 0 THEN N'TAMAM' ELSE N'HATA' END AS sonuc FROM @r ORDER BY sonuc DESC, kontrol;

-- Bilgi (hata değil): geri yükleme sonrası gözden geçirilecekler
SELECT N'saklama süresi geçmiş oturum (RetentionJob 15 dk içinde siler)' AS bilgi, COUNT(*) AS adet FROM tryon_sessions WHERE retention_until <= SYSDATETIMEOFFSET() AND status <> N'Purged'
UNION ALL SELECT N'etkin TV eşleştirmesi (gözden geçirin: yedekten sonra iptal edilenler geri gelmiş olabilir)', COUNT(*) FROM display_devices WHERE revoked_at IS NULL
UNION ALL SELECT N'etkin kullanıcı (gözden geçirin: yedekten sonra pasife alınanlar geri gelmiş olabilir)', COUNT(*) FROM users WHERE is_active = 1;

IF EXISTS (SELECT 1 FROM @r WHERE ihlal <> 0) THROW 51350, N'Geri yükleme sonrası denetim BAŞARISIZ: yukarıdaki HATA satırlarına bakın; veritabanı üretime alınmamalı.', 1;
IF N'$(RunCheckDb)' = N'1' DBCC CHECKDB (N'$(Database)') WITH NO_INFOMSGS;
PRINT N'Geri yükleme sonrası denetim TAMAM.';
GO
