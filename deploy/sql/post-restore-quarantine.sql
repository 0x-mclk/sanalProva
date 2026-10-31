-- Geri yükleme SONRASI karantina (Pilot Faz A): yedek ile "şimdi" arasındaki farkın güvenlik ve mali etkilerini kapatır. API/işçi DURDURULMUŞKEN, yeni veritabanı
-- üretime alınmadan ÖNCE çalıştırılır; idempotenttir. Hiçbir veri silinmez; yalnızca durum değiştirir.
--   sqlcmd -S sqlserver,1433 -U sa -C -b -v Database=sanalprova_restore_20261101 -i post-restore-quarantine.sql
-- NEDEN (yedek, yedekten sonraki olayları bilmez):
--  1) Yedekte Queued/Running görünen işler yedekten SONRA tamamlanmış/ücretlenmiş olabilir; işçi bunları yeniden çalıştırırsa sağlayıcıya İKİNCİ (ücretli) çağrı gider.
--     → Gönderim kanıtı olan işler "Failed/OutcomeUncertain" (süpürücü en kötü durumla belirsiz maliyet kaydı yazar, kredi mutabakata kadar bloke), diğerleri "Cancelled".
--  2) Yedekten sonra kullanılmış kurulum/şifre sıfırlama bağlantıları yeniden "kullanılmamış" görünür → tüm açık bağlantılar iptal edilir (yöneticiler yeniden üretir).
--  3) Yedekten sonra pasife alınan kullanıcı/silinen yetki geri gelmiş olabilir; açık oturumlar da geçersiz kılınır → tüm güvenlik damgaları yenilenir (herkes yeniden giriş yapar).
--     Pasife alma/parola değişiklikleri yedek zamanındaki duruma döner: operatör kullanıcı listesini gözden geçirmelidir (docs/19).
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
USE [$(Database)];
GO
IF N'$(Database)' IN (N'sanalprova_dev', N'sanalprova_test', N'sanalprova_e2e', N'master', N'tempdb', N'model', N'msdb')
    THROW 51340, N'Karantina korumalı bir veritabanında çalıştırılmaz.', 1;
GO
BEGIN TRANSACTION;
DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();

DECLARE @uncertain int, @cancelled int, @tokens int, @users int;

UPDATE generation_jobs
   SET status = N'Failed', error_code = N'OutcomeUncertain', lease_until = NULL, finished_at = @now, updated_at = @now,
       error_message = N'Yedekten geri yükleme sonrası: sonuç bilinmiyor; çift ücret olmaması için otomatik yeniden denenmedi.'
 WHERE status IN (N'Queued', N'Running') AND (provider_submit_started_at IS NOT NULL OR provider_request_id IS NOT NULL);
SET @uncertain = @@ROWCOUNT;

UPDATE generation_jobs
   SET status = N'Cancelled', lease_until = NULL, finished_at = @now, updated_at = @now
 WHERE status IN (N'Queued', N'Running');
SET @cancelled = @@ROWCOUNT;

UPDATE account_setup_tokens SET revoked_at = @now WHERE used_at IS NULL AND revoked_at IS NULL;
SET @tokens = @@ROWCOUNT;

UPDATE users SET security_stamp = CONVERT(nvarchar(36), NEWID()), updated_at = @now;
SET @users = @@ROWCOUNT;
COMMIT TRANSACTION;

PRINT N'Karantina: belirsize çevrilen iş = ' + CAST(@uncertain AS nvarchar(20)) + N'; iptal edilen (gönderilmemiş) iş = ' + CAST(@cancelled AS nvarchar(20))
    + N'; iptal edilen açık bağlantı = ' + CAST(@tokens AS nvarchar(20)) + N'; oturumu sıfırlanan kullanıcı = ' + CAST(@users AS nvarchar(20)) + N'.';
GO
