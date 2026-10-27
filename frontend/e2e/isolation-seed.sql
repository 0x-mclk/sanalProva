-- E2E izolasyon verisi (yalnızca sanalprova_e2e): aynı firmada ikinci şube + ayrı bir firma. Gerçek kişi verisi yok; parola hash'i demo yöneticiden kopyalanır.
SET NOCOUNT ON;
DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @org uniqueidentifier = (SELECT id FROM organizations WHERE slug = N'lal');
DECLARE @mgr uniqueidentifier = (SELECT id FROM users WHERE normalized_email = N'YONETICI@LAL.LOCAL');
IF @org IS NULL OR @mgr IS NULL THROW 50001, 'Demo veri bulunamadi (tohumlayici calismamis).', 1;

IF NOT EXISTS (SELECT 1 FROM stores WHERE organization_id = @org AND code = N'BBK')
BEGIN
    DECLARE @s2 uniqueidentifier = NEWID();
    INSERT INTO stores (id, organization_id, name, code, timezone, daily_generation_quota, is_active, created_at, updated_at)
    SELECT @s2, @org, N'Bebek Şube', N'BBK', timezone, daily_generation_quota, 1, @now, @now FROM stores WHERE organization_id = @org AND code = N'NST';
    INSERT INTO user_store_roles (user_id, store_id, organization_id, role) VALUES (@mgr, @s2, @org, N'Manager');
END

IF NOT EXISTS (SELECT 1 FROM organizations WHERE slug = N'yabanci')
BEGIN
    DECLARE @o2 uniqueidentifier = NEWID(), @s3 uniqueidentifier = NEWID(), @u3 uniqueidentifier = NEWID();
    INSERT INTO organizations (id, name, slug, is_active, created_at, updated_at, settings)
    SELECT @o2, N'Yabancı Butik', N'yabanci', 1, @now, @now, settings FROM organizations WHERE id = @org;
    INSERT INTO stores (id, organization_id, name, code, timezone, daily_generation_quota, is_active, created_at, updated_at)
    SELECT @s3, @o2, N'Yabancı Şube', N'YBC', timezone, daily_generation_quota, 1, @now, @now FROM stores WHERE organization_id = @org AND code = N'NST';
    INSERT INTO users (id, organization_id, display_name, pin_hash, is_active, created_at, updated_at, user_name, normalized_user_name, email, normalized_email,
                       email_confirmed, password_hash, security_stamp, concurrency_stamp, phone_number, phone_number_confirmed, two_factor_enabled, lockout_end, lockout_enabled, access_failed_count, is_platform_admin)
    SELECT @u3, @o2, N'Yabancı Yönetici', NULL, 1, @now, @now, N'baska@firma.local', N'BASKA@FIRMA.LOCAL', N'baska@firma.local', N'BASKA@FIRMA.LOCAL',
           1, password_hash, CONVERT(nvarchar(50), NEWID()), CONVERT(nvarchar(50), NEWID()), NULL, 0, 0, NULL, lockout_enabled, 0, 0 FROM users WHERE id = @mgr;
    INSERT INTO user_store_roles (user_id, store_id, organization_id, role) VALUES (@u3, @s3, @o2, N'Manager');
END
