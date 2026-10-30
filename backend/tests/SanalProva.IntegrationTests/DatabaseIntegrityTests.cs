using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Generation;

namespace SanalProva.IntegrationTests;

/// <summary>Bütünlük testlerinin ortak laboratuvar veritabanı: bir kez kurulur, testler birbirine karışmasın diye her biri kendi rastgele verisini üretir.</summary>
public sealed class IntegrityLab : IAsyncLifetime
{
    internal MigrationLab? Lab { get; private set; }

    internal RowFactory? Rows { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!MigrationLab.IsConfigured)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        Lab = await MigrationLab.CreateAsync(ct);
        await Lab.MigrateAsync(ct);
        Rows = await RowFactory.LoadAsync(Lab.MigratorCs, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Lab is not null)
        {
            await Lab.DisposeAsync();
        }
    }
}

/// <summary>
/// M3.3: veri bütünlüğünün <b>SQL Server tarafından</b> korunduğunun kanıtı. Uygulama kodu atlansa bile (elle SQL, hatalı kod, yarış) kurallar veritabanında
/// reddedilir. Her kanıt gerçek DML ile yapılır (ihlal denemesi → beklenen hata numarası); şemadaki tüm FK'lar ve tüm enum sütunları kataloğa bakılarak
/// veri odaklı sınanır: yeni tablo/FK/enum eklenince testler onu da kapsar (ya da bilinçli güncelleme ister).
/// </summary>
public sealed class DatabaseIntegrityTests(IntegrityLab fixture) : IClassFixture<IntegrityLab>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MigrationLab Lab
    {
        get
        {
            Assert.SkipUnless(MigrationLab.IsConfigured, "Migration laboratuvarı yapılandırılmamış (SANALPROVA_LAB_ADMIN).");
            return fixture.Lab!;
        }
    }

    private RowFactory Rows => fixture.Rows!;

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        Guid g => $"'{g}'",
        string s => $"N'{s.Replace("'", "''", StringComparison.Ordinal)}'",
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture)!,
        _ => throw new InvalidOperationException($"Desteklenmeyen sabit: {value.GetType()}"),
    };

    private string Where(string table, Dictionary<string, object?> row) =>
        string.Join(" AND ", Rows.PrimaryKey(table).Select(c => $"[{c}] = {Literal(row[c])}"));

    private async Task<SqlException> RejectedAsync(string sql, params int[] numbers)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => Lab.ExecuteAsync(sql, ct: Ct));
        Assert.True(numbers.Contains(ex.Number), $"Beklenen hata {string.Join('/', numbers)}, gelen {ex.Number}: {ex.Message} ({sql})");
        return ex;
    }

    // ------------------------------------------------------------------ yabancı anahtarlar: NO ACTION / CASCADE / SET NULL

    /// <summary>Tasarım niyeti: silme davranışlarının tamamı. Yeni FK eklenince bu liste bilinçli olarak güncellenmeden test kırılır.</summary>
    private static readonly Dictionary<string, string> ExpectedForeignKeys = new(StringComparer.Ordinal)
    {
        // Tasarım niyeti (firma tutarlılığı bileşik FK'larla veritabanında zorunlu; silme davranışları bileşik FK'lardan ÖNCEKİYLE aynıdır):
        //  - KVKK silmesinin kökü oturum: onay, fotoğraf, seçim, kalem, iş ve sonuç görseli zincirleme gider (CASCADE); bunların arasındaki ikinci yollar NO ACTION
        //    (SQL Server tek zincirleme yol kabul eder; aynı DELETE deyiminde zaten giderler).
        //  - Maliyet kayıtları oturum silinse de kalır: iş bağlantısı SET NULL (tek sütunlu FK) + "aynı firmada" kuralı için bileşik NO ACTION eşi (NO_ACTION_COMPANION: bağlantı boşalınca devre dışı).
        //  - Aynı kural TV için: oturum silinince TV bağlantısı boşalır; TV ve oturum aynı şubede/firmada olmalıdır.
        //  - Firma/şube/kullanıcı/katalog: referans edilen ana kayıt silinemez (NO ACTION); ürün silinince varyant, görsel, bileşenler gider; kullanıcı/şube silinince rol satırları gider.
        //  - M5.1: kurulum token'ı kullanıcıyla birlikte gider (CASCADE, ephemeral); sözleşme kabulü append-only kayıt, kullanıcı silinse de kalır (NO ACTION).
        ["fk_account_setup_tokens_asp_net_users_user_id_organization_id"] = "CASCADE",
        ["fk_account_setup_tokens_organizations_organization_id"] = "NO_ACTION",
        ["fk_ai_cost_reconciliations_ai_usage_logs_usage_log_id_organization_id"] = "NO_ACTION",
        ["fk_ai_usage_logs_ai_usage_logs_adjusts_log_id_organization_id"] = "NO_ACTION",
        ["fk_ai_usage_logs_generation_jobs_job_id"] = "SET_NULL",
        ["fk_ai_usage_logs_generation_jobs_job_id_organization_id"] = "NO_ACTION_COMPANION",
        ["fk_ai_usage_logs_stores_store_id_organization_id"] = "NO_ACTION",
        ["fk_audit_logs_organizations_organization_id"] = "NO_ACTION",
        ["fk_audit_logs_stores_store_id_organization_id"] = "NO_ACTION",
        ["fk_categories_organizations_organization_id"] = "NO_ACTION",
        ["fk_consent_records_asp_net_users_recorded_by_user_id_organization_id"] = "NO_ACTION",
        ["fk_consent_records_tryon_sessions_session_id_organization_id"] = "CASCADE",
        ["fk_display_devices_stores_store_id_organization_id"] = "NO_ACTION",
        ["fk_display_devices_try_on_sessions_current_session_id"] = "SET_NULL",
        ["fk_display_devices_try_on_sessions_current_session_id_store_id_organization_id"] = "NO_ACTION_COMPANION",
        ["fk_display_pairings_display_devices_claimed_device_id"] = "SET_NULL",
        ["fk_generated_images_generated_images_parent_image_id_organization_id"] = "NO_ACTION",
        ["fk_generated_images_generation_jobs_job_id_organization_id"] = "NO_ACTION",
        ["fk_generated_images_stored_files_file_id_organization_id"] = "NO_ACTION",
        ["fk_generated_images_try_on_sessions_session_id_organization_id"] = "CASCADE",
        ["fk_generation_jobs_generated_images_base_image_id_organization_id"] = "NO_ACTION",
        ["fk_generation_jobs_outfit_selections_selection_id_organization_id"] = "NO_ACTION",
        ["fk_generation_jobs_session_photos_source_photo_id_organization_id"] = "NO_ACTION",
        ["fk_generation_jobs_stores_store_id_organization_id"] = "NO_ACTION",
        ["fk_generation_jobs_try_on_sessions_session_id_store_id_organization_id"] = "CASCADE",
        ["fk_organization_agreement_acceptances_asp_net_users_user_id_organization_id"] = "NO_ACTION",
        ["fk_organization_agreement_acceptances_organizations_organization_id"] = "NO_ACTION",
        ["fk_outfit_selection_items_categories_category_id_organization_id"] = "NO_ACTION",
        ["fk_outfit_selection_items_outfit_selections_selection_id_organization_id"] = "CASCADE",
        ["fk_outfit_selection_items_product_variants_variant_id_organization_id"] = "NO_ACTION",
        ["fk_outfit_selection_items_products_product_id_organization_id"] = "NO_ACTION",
        ["fk_outfit_selections_try_on_sessions_session_id_organization_id"] = "CASCADE",
        ["fk_outfit_set_components_product_variants_component_variant_id_organization_id"] = "NO_ACTION",
        ["fk_outfit_set_components_products_component_product_id_organization_id"] = "NO_ACTION",
        ["fk_outfit_set_components_products_set_product_id_organization_id"] = "CASCADE",
        ["fk_product_images_product_variants_variant_id_organization_id"] = "NO_ACTION",
        ["fk_product_images_products_product_id_organization_id"] = "CASCADE",
        ["fk_product_images_stored_files_file_id_organization_id"] = "NO_ACTION",
        ["fk_product_variants_products_product_id_organization_id"] = "CASCADE",
        ["fk_products_categories_category_id_organization_id"] = "NO_ACTION",
        ["fk_products_organizations_organization_id"] = "NO_ACTION",
        ["fk_session_photos_stored_files_file_id_organization_id"] = "NO_ACTION",
        ["fk_session_photos_tryon_sessions_session_id_organization_id"] = "CASCADE",
        ["fk_stored_files_organizations_organization_id"] = "NO_ACTION",
        ["fk_stores_organizations_organization_id"] = "NO_ACTION",
        ["fk_tryon_sessions_asp_net_users_created_by_user_id_organization_id"] = "NO_ACTION",
        ["fk_tryon_sessions_stores_store_id_organization_id"] = "NO_ACTION",
        ["fk_user_claims_users_user_id"] = "CASCADE",
        ["fk_user_logins_users_user_id"] = "CASCADE",
        ["fk_user_store_roles_asp_net_users_user_id_organization_id"] = "CASCADE",
        ["fk_user_store_roles_organizations_organization_id"] = "NO_ACTION",
        ["fk_user_store_roles_stores_store_id_organization_id"] = "CASCADE",
        ["fk_user_tokens_users_user_id"] = "CASCADE",
        ["fk_users_organizations_organization_id"] = "NO_ACTION",
    };

    [Fact]
    public async Task Her_yabanci_anahtar_beklenen_silme_davranisini_uygular_yetim_satir_eklenemez()
    {
        _ = Lab;
        var actual = Rows.ForeignKeys.ToDictionary(f => f.Name, f => f.Action, StringComparer.Ordinal);
        Assert.Equal(ExpectedForeignKeys.Keys.Order(StringComparer.Ordinal).ToArray(), actual.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ExpectedForeignKeys.Count, Rows.ForeignKeys.Count);

        var problems = new List<string>();
        foreach (var fk in Rows.ForeignKeys)
        {
            var expected = ExpectedForeignKeys[fk.Name];
            var companion = expected == "NO_ACTION_COMPANION"; // SET NULL tek sütunlu FK'nın bileşik eşi: veritabanında NO ACTION, davranışı "bağlantıyı boşaltmak"
            if (actual[fk.Name] != (companion ? "NO_ACTION" : expected))
            {
                problems.Add($"{fk.Name}: veritabanında {actual[fk.Name]}, beklenen {expected}");
                continue;
            }

            // (1) Yetim satır: geçerli bir çocuğun yabancı anahtar sütunu var olmayan bir ebeveyne çevrilemez. (Bileşik FK'larda firma sütunu başka FK'larla ortaktır; bu yüzden
            //     yalnızca kimlik sütunu değiştirilir ve hata, aynı çocuk tablosundaki, değişen sütunu KULLANAN (ilk sütun olarak veya bileşik anahtarın bir parçası olarak)
            //     FK'lardan biri olmalıdır: SQL Server birden fazla kısıt ihlal edilince hangisini bildireceğini constraint object_id sırasına göre seçer, "ilk sütun"
            //     eşleşmesi tek başına yeterli değildir — ör. account_setup_tokens'ta organization_id hem kendi düz FK'sının hem user_id ile bileşik FK'nın parçasıdır.)
            var valid = await Rows.CreateAsync(fk.Child, null, Ct);
            var siblingNames = Rows.ForeignKeys.Where(f => f.Child == fk.Child && f.ChildColumns.Contains(fk.ChildColumns[0])).Select(f => f.Name).ToList();
            var orphan = await Assert.ThrowsAsync<SqlException>(() => Lab.ExecuteAsync($"UPDATE [{fk.Child}] SET [{fk.ChildColumns[0]}] = '{Guid.NewGuid()}' WHERE {Where(fk.Child, valid)}", ct: Ct));
            if (orphan.Number != 547 || !siblingNames.Any(n => orphan.Message.Contains(n, StringComparison.Ordinal)))
            {
                problems.Add($"{fk.Name}: yetim bağlantı beklenen FK hatasıyla reddedilmedi ({orphan.Number}: {orphan.Message})");
            }

            // (2) Ebeveyn silme: gerçek DELETE ile NO ACTION / CASCADE / SET NULL davranışı
            var parent = await Rows.CreateAsync(fk.Parent, null, Ct);
            var child = await Rows.CreateAsync(fk.Child, fk.ChildColumns.Select((c, i) => (c, parent[fk.ParentColumns[i]])).ToDictionary(p => p.c, p => p.Item2), Ct);
            var parentFilter = string.Join(" AND ", fk.ParentColumns.Select(c => $"[{c}] = {Literal(parent[c])}"));
            var childKey = Where(fk.Child, child);
            var delete = $"DELETE FROM [{fk.Parent}] WHERE {parentFilter}";

            if (fk.Action == "NO_ACTION" && !companion)
            {
                var blocked = await Assert.ThrowsAsync<SqlException>(() => Lab.ExecuteAsync(delete, ct: Ct));
                if (blocked.Number != 547)
                {
                    problems.Add($"{fk.Name}: silme engellenmedi/yanlış hata ({blocked.Number})");
                }

                if (await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM [{fk.Parent}] WHERE {parentFilter}", ct: Ct) != 1 || await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM [{fk.Child}] WHERE {childKey}", ct: Ct) != 1)
                {
                    problems.Add($"{fk.Name}: engellenen silme satırları değiştirdi");
                }
            }
            else
            {
                await Lab.ExecuteAsync(delete, ct: Ct);
                var childCount = await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM [{fk.Child}] WHERE {childKey}", ct: Ct);
                var expectedChildren = fk.Action == "CASCADE" ? 0 : 1; // SET NULL ve eşleri: çocuk kalır
                if (childCount != expectedChildren)
                {
                    problems.Add($"{fk.Name}: {fk.Action} sonrası çocuk sayısı {childCount}, beklenen {expectedChildren}");
                }

                if ((fk.Action == "SET_NULL" || companion) && await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM [{fk.Child}] WHERE {childKey} AND [{fk.ChildColumns[0]}] IS NULL", ct: Ct) != 1)
                {
                    problems.Add($"{fk.Name}: SET NULL bağlantıyı boşaltmadı"); // yalnızca kimlik sütunu boşalır; firma/şube sütunları dokunulmaz
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Oturum_silme_butun_musteri_verisini_zincirleme_siler_muhasebe_ve_dosya_kayitlari_kalir()
    {
        _ = Lab;
        // Gerçek bir firma/şube/kullanıcı: dosya kayıtlarının firma FK'sı var (oturum tablosunun yok: aşağıdaki bulgu)
        var org = (await Rows.CreateAsync("organizations", null, Ct))["id"];
        var store = (await Rows.CreateAsync("stores", new Dictionary<string, object?> { ["organization_id"] = org }, Ct))["id"];
        var user = (await Rows.CreateAsync("users", new Dictionary<string, object?> { ["organization_id"] = org }, Ct))["id"];
        var session = await Rows.CreateAsync("tryon_sessions", new Dictionary<string, object?> { ["organization_id"] = org, ["store_id"] = store, ["created_by_user_id"] = user }, Ct);
        var id = session["id"]!;

        Task<Dictionary<string, object?>> Child(string table, params (string, object?)[] extra) =>
            Rows.CreateAsync(table, extra.ToDictionary(e => e.Item1, e => e.Item2, StringComparer.Ordinal), Ct);

        var consent = await Child("consent_records", ("session_id", id), ("organization_id", org), ("recorded_by_user_id", user));
        var photoFile = await Child("stored_files", ("organization_id", org));
        var photo = await Child("session_photos", ("session_id", id), ("organization_id", org), ("file_id", photoFile["id"]));
        var selection = await Child("outfit_selections", ("session_id", id), ("organization_id", org));
        var item = await Child("outfit_selection_items", ("selection_id", selection["id"]), ("organization_id", org));
        var job = await Child("generation_jobs", ("session_id", id), ("organization_id", org), ("store_id", store), ("requested_by_user_id", user), ("selection_id", selection["id"]), ("source_photo_id", photo["id"]));
        var resultFile = await Child("stored_files", ("organization_id", org));
        var image = await Child("generated_images", ("session_id", id), ("organization_id", org), ("job_id", job["id"]), ("file_id", resultFile["id"]));
        var usage = await Child("ai_usage_logs", ("organization_id", org), ("store_id", store), ("job_id", job["id"]), ("estimated_cost_usd", 0.15m));
        var device = await Child("display_devices", ("store_id", store), ("organization_id", org), ("current_session_id", id));

        // Oturum silinirken çözülmesi gereken NO ACTION bağlantıları (iş → seçim/fotoğraf, görsel → iş) aynı deyimde gider
        await Lab.ExecuteAsync($"DELETE FROM tryon_sessions WHERE id = {Literal(id)}", ct: Ct);

        async Task<int> Count(string table, Dictionary<string, object?> row) => await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM [{table}] WHERE {Where(table, row)}", ct: Ct);
        Assert.Equal(0, await Count("consent_records", consent));
        Assert.Equal(0, await Count("session_photos", photo));
        Assert.Equal(0, await Count("outfit_selections", selection));
        Assert.Equal(0, await Count("outfit_selection_items", item));
        Assert.Equal(0, await Count("generation_jobs", job));
        Assert.Equal(0, await Count("generated_images", image));

        // Kalanlar: maliyet kaydı (iş bağlantısı koptu), TV cihazı (oturumdan ayrıldı), dosya kayıtları (depo temizliği SessionPurger'ın işi)
        Assert.Equal(1, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_usage_logs WHERE id = {Literal(usage["id"])} AND job_id IS NULL AND estimated_cost_usd = 0.15", ct: Ct));
        Assert.Equal(1, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM display_devices WHERE id = {Literal(device["id"])} AND current_session_id IS NULL", ct: Ct));
        Assert.Equal(2, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM stored_files WHERE id IN ({Literal(photoFile["id"])}, {Literal(resultFile["id"])})", ct: Ct));

        // Dosya kaydı, ona bağlı fotoğraf varken silinemez (önce oturum, sonra dosya sırası zorunlu); oturum gittikten sonra silinebilir
        var otherFile = await Child("stored_files", ("organization_id", org));
        var otherSession = await Rows.CreateAsync("tryon_sessions", new Dictionary<string, object?> { ["organization_id"] = org, ["store_id"] = store, ["created_by_user_id"] = user }, Ct);
        await Child("session_photos", ("session_id", otherSession["id"]), ("organization_id", org), ("file_id", otherFile["id"]));
        await RejectedAsync($"DELETE FROM stored_files WHERE id = {Literal(otherFile["id"])}", 547);
        await Lab.ExecuteAsync($"DELETE FROM tryon_sessions WHERE id = {Literal(otherSession["id"])}", ct: Ct);
        await Lab.ExecuteAsync($"DELETE FROM stored_files WHERE id = {Literal(otherFile["id"])}", ct: Ct);
    }

    // ------------------------------------------------------------------ enum sütunları: CHECK + EF dönüşümü

    [Fact]
    public async Task Her_enum_sutunu_butun_uyelerini_kabul_eder_gecersiz_degeri_reddeder_ve_adlar_sutuna_sigar()
    {
        _ = Lab;
        await using var db = Lab.OpenMigrator();
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var checks = 0;
        var problems = new List<string>();
        var processed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var table in model.Tables.Where(t => t.Name != "__ef_migrations_history"))
        {
            foreach (var column in table.Columns)
            {
                var clr = column.PropertyMappings.Count > 0 ? column.PropertyMappings[0].Property.ClrType : null;
                var enumType = clr is null ? null : Nullable.GetUnderlyingType(clr) ?? clr;
                if (enumType is null || !enumType.IsEnum)
                {
                    continue;
                }

                checks++;
                processed.Add($"{column.PropertyMappings[0].Property.DeclaringType.DisplayName()}.{column.PropertyMappings[0].Property.Name}");
                var meta = Rows.Columns[table.Name].Single(c => c.Name == column.Name);
                var row = await Rows.CreateAsync(table.Name, null, Ct);
                var where = Where(table.Name, row);
                var constraint = $"ck_{table.Name}_{column.Name}";

                foreach (var member in Enum.GetNames(enumType))
                {
                    if (member.Length > meta.MaxLength / 2)
                    {
                        problems.Add($"{table.Name}.{column.Name}: '{member}' sütuna sığmıyor ({meta.MaxLength / 2})");
                        continue;
                    }

                    try
                    {
                        await Lab.ExecuteAsync($"UPDATE [{table.Name}] SET [{column.Name}] = N'{member}' WHERE {where}", ct: Ct);
                    }
                    catch (SqlException ex)
                    {
                        problems.Add($"{table.Name}.{column.Name}: geçerli üye '{member}' reddedildi → {ex.Message}");
                    }
                }

                try
                {
                    await Lab.ExecuteAsync($"UPDATE [{table.Name}] SET [{column.Name}] = N'ZZ_GECERSIZ' WHERE {where}", ct: Ct);
                    problems.Add($"{table.Name}.{column.Name}: geçersiz değer KABUL EDİLDİ");
                }
                catch (SqlException ex) when (ex.Number == 547 && ex.Message.Contains(constraint, StringComparison.Ordinal))
                {
                }
                catch (SqlException ex)
                {
                    problems.Add($"{table.Name}.{column.Name}: geçersiz değer beklenen CHECK ({constraint}) yerine {ex.Number} ile reddedildi");
                }

                if (meta.Nullable)
                {
                    await Lab.ExecuteAsync($"UPDATE [{table.Name}] SET [{column.Name}] = NULL WHERE {where}", ct: Ct); // NULL, CHECK'i geçer (bilinçli: "yok" durumu)
                }
            }
        }

        // Döngü hiçbir enum sütununu atlamadı. Bulgu: JSON belgesi içinde saklanan iki enum (TV durumu kipi, oturum taslağı kipi) sütun değil, bu yüzden
        // veritabanı CHECK'i yoktur; geçerliliği yalnızca uygulama (System.Text.Json dönüşümü) denetler.
        var modelEnums = db.Model.GetEntityTypes().SelectMany(e => e.GetProperties().Select(p => (key: $"{e.DisplayName()}.{p.Name}", ClrType: p.ClrType)))
            .Where(x => (Nullable.GetUnderlyingType(x.ClrType) ?? x.ClrType).IsEnum).Select(x => x.key).ToList();
        Assert.Equal(["DisplayState.Mode", "SessionDraft.Mode"], modelEnums.Except(processed).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(processed.Count, checks);
        Assert.True(checks >= 15, $"Beklenenden az enum sütunu bulundu: {checks}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Enum_CHECK_kisitlari_buyuk_kucuk_harfe_duyarsizdir_EF_bu_degeri_okur()
    {
        _ = Lab;
        var job = await Rows.CreateAsync("generation_jobs", null, Ct);
        var id = (Guid)job["id"]!;

        // Veritabanı collation'ı CI: elle yazılan 'queued' CHECK'ten geçer. Bu, sıradan SQL ile yazılan değerin enum'a dönüşebilmesi demektir.
        await Lab.ExecuteAsync($"UPDATE generation_jobs SET status = N'queued' WHERE id = '{id}'", ct: Ct);
        await using var db = MigrationLab.OpenDb(Lab.AppCs);
        var read = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == id, Ct);
        Assert.Equal(GenerationJobStatus.Queued, read.Status);

        // Aynı CI davranışı filtreli indeks kapsamı için de geçerlidir: 'queued' etkin iş sayılır (benzersizlik kuralı büyük/küçük harften etkilenmez)
        Assert.Equal(1, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM generation_jobs WHERE id = '{id}' AND status IN (N'Queued', N'Running', N'Succeeded')", ct: Ct));
    }

    // ------------------------------------------------------------------ para (decimal) ve zaman (datetimeoffset)

    [Fact]
    public async Task Para_alanlari_yalnizca_decimal_dir_yuvarlama_sessizdir_tasma_hatadir()
    {
        _ = Lab;

        // Hiçbir sütun kayan nokta/money değil; tüm *_usd sütunları decimal(10,4)
        Assert.Equal(0, await Lab.ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id JOIN sys.tables t ON t.object_id = c.object_id WHERE ty.name IN (N'float', N'real', N'money', N'smallmoney') AND t.is_ms_shipped = 0", ct: Ct));
        var usd = await Lab.ScalarAsync<string>("SELECT STRING_AGG(t.name + N'.' + c.name + N':' + ty.name + N'(' + CAST(c.precision AS nvarchar(5)) + N',' + CAST(c.scale AS nvarchar(5)) + N')', N';') WITHIN GROUP (ORDER BY t.name, c.name) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id JOIN sys.tables t ON t.object_id = c.object_id WHERE c.name LIKE N'%[_]usd'", ct: Ct);
        Assert.All(usd!.Split(';'), s => Assert.EndsWith(":decimal(10,4)", s, StringComparison.Ordinal));
        Assert.True(usd.Split(';').Length >= 5, usd);

        var log = await Rows.CreateAsync("ai_usage_logs", new Dictionary<string, object?> { ["estimated_cost_usd"] = 0.1m }, Ct);
        var where = Where("ai_usage_logs", log);

        // Sessiz yuvarlama: 4 haneden fazlası hata vermeden yarıma yukarı yuvarlanır (uygulama 4 haneyi doğrular; veritabanı bunu yakalamaz)
        await Lab.ExecuteAsync($"UPDATE ai_usage_logs SET estimated_cost_usd = 0.12345 WHERE {where}", ct: Ct);
        Assert.Equal(0.1235m, await Lab.ScalarAsync<decimal>($"SELECT estimated_cost_usd FROM ai_usage_logs WHERE {where}", ct: Ct));

        // Taşma hatadır (aritmetik taşma 8115); sınır değerler kabul edilir, negatif düzeltme satırları geçerlidir
        await RejectedAsync($"UPDATE ai_usage_logs SET estimated_cost_usd = 1000000.0000 WHERE {where}", 8115);
        await Lab.ExecuteAsync($"UPDATE ai_usage_logs SET estimated_cost_usd = 999999.9999 WHERE {where}", ct: Ct);
        await Lab.ExecuteAsync($"UPDATE ai_usage_logs SET estimated_cost_usd = -0.0300 WHERE {where}", ct: Ct);

        // Toplama kayan nokta hatası üretmez ve EF ile gidiş-dönüş tam eşit
        Assert.Equal(0.3m, await Lab.ScalarAsync<decimal>("SELECT CAST(0.1 AS decimal(10,4)) + CAST(0.1 AS decimal(10,4)) + CAST(0.1 AS decimal(10,4))", ct: Ct));
        await using var db = MigrationLab.OpenDb(Lab.AppCs);
        var entity = await db.AiUsageLogs.IgnoreQueryFilters().SingleAsync(l => l.Id == (Guid)log["id"]!, Ct);
        Assert.Equal(-0.03m, entity.EstimatedCostUsd);
    }

    [Fact]
    public async Task Datetimeoffset_ofset_korunur_karsilastirma_ve_siralama_ani_esas_alir_tik_hassasiyeti_kaybolmaz()
    {
        _ = Lab;
        var log = await Rows.CreateAsync("ai_usage_logs", null, Ct);
        var where = Where("ai_usage_logs", log);
        var local = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(3)); // 07:00 UTC
        await using (var connection = new SqlConnection(Lab.MigratorCs))
        {
            await connection.OpenAsync(Ct);
            await using var command = new SqlCommand($"UPDATE ai_usage_logs SET created_at = @at WHERE {where}", connection);
            command.Parameters.AddWithValue("@at", local);
            await command.ExecuteNonQueryAsync(Ct);
        }

        // Ofset saklanır, ama eşitlik/sıralama anlıktır: +03:00 10:00 = 07:00Z
        Assert.Equal("+03:00", await Lab.ScalarAsync<string>($"SELECT DATENAME(TZoffset, created_at) FROM ai_usage_logs WHERE {where}", ct: Ct));
        Assert.Equal(1, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_usage_logs WHERE {where} AND created_at = '2026-10-27T07:00:00+00:00'", ct: Ct));
        Assert.Equal(1, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_usage_logs WHERE {where} AND created_at >= '2026-10-27T00:00:00+00:00' AND created_at < '2026-10-28T00:00:00+00:00'", ct: Ct));
        Assert.Equal(0, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_usage_logs WHERE {where} AND created_at >= '2026-10-27T00:00:00+03:00' AND created_at < '2026-10-27T03:00:00+03:00'", ct: Ct));

        // 00:30 +03:00 önceki UTC gününe düşer: günlük bütçe penceresi UTC anına göre kesilir
        await Lab.ExecuteAsync($"UPDATE ai_usage_logs SET created_at = '2026-10-27T00:30:00+03:00' WHERE {where}", ct: Ct);
        Assert.Equal(1, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_usage_logs WHERE {where} AND created_at < '2026-10-27T00:00:00+00:00'", ct: Ct));

        // EF gidiş-dönüş: 100 ns tik hassasiyeti ve ofset korunur; uygulama anı UtcDateTime ile karşılaştırmalı
        var precise = new DateTimeOffset(2026, 10, 27, 7, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        await using var db = MigrationLab.OpenDb(Lab.AppCs);
        var entity = await db.AiUsageLogs.IgnoreQueryFilters().SingleAsync(l => l.Id == (Guid)log["id"]!, Ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ai_usage_logs SET occurred_at = {precise} WHERE id = {entity.Id}", Ct);
        db.ChangeTracker.Clear();
        var read = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == entity.Id, Ct);
        Assert.Equal(precise.UtcTicks, read.OccurredAt!.Value.UtcTicks);
        Assert.Equal(TimeSpan.FromHours(3), read.CreatedAt.Offset); // saklanan ofset: uygulama yalnızca UTC yazar, ofsetli veri dışarıdan gelirse korunur
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 21, 30, 0, TimeSpan.Zero).UtcDateTime, read.CreatedAt.UtcDateTime);
    }

    // ------------------------------------------------------------------ Türkçe collation

    [Fact]
    public async Task Turkce_collation_yalnizca_urun_adinda_ve_i_ı_I_İ_kurallari_dogru_calisir()
    {
        _ = Lab;
        Assert.Equal(
            ["products.name:Turkish_100_CI_AS"],
            (await Lab.ScalarAsync<string>("SELECT STRING_AGG(t.name + N'.' + c.name + N':' + c.collation_name, N';') FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE c.collation_name IS NOT NULL AND c.collation_name <> CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(100)) AND t.is_ms_shipped = 0", ct: Ct))!.Split(';'));

        var org = (await Rows.CreateAsync("organizations", null, Ct))["id"];
        var category = (await Rows.CreateAsync("categories", new Dictionary<string, object?> { ["organization_id"] = org }, Ct))["id"];
        async Task<Guid> Product(string name) => (Guid)(await Rows.CreateAsync("products", new Dictionary<string, object?> { ["organization_id"] = org, ["category_id"] = category, ["name"] = name }, Ct))["id"]!;
        var kina = await Product("Kına Elbisesi");
        var isik = await Product("ışık Takım");

        async Task<bool> Same(Guid id, string literal) => await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM products WHERE id = '{id}' AND name = N'{literal}'", ct: Ct) == 1;
        async Task<bool> Like(Guid id, string pattern) => await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM products WHERE id = '{id}' AND name LIKE N'{pattern}'", ct: Ct) == 1;

        // Türkçe CI: I ≡ ı ve İ ≡ i; ama I ≢ i ve ı ≢ İ
        Assert.True(await Same(kina, "KINA ELBİSESİ"), "kına elbisesi = KINA ELBİSESİ");
        Assert.True(await Same(kina, "kına elbisesi"));
        Assert.False(await Same(kina, "KİNA ELBİSESİ"), "ı ile İ eşit değildir");
        Assert.False(await Same(kina, "kina elbisesi"), "ASCII 'i' ile 'ı' farklı harftir");
        Assert.True(await Same(isik, "IŞIK TAKIM"));
        Assert.True(await Like(kina, "%kına%"));
        Assert.True(await Like(kina, "%KINA%"));
        Assert.False(await Like(kina, "%kina%"), "Dikkat: 'kina' yazan kullanıcı 'Kına' ürününü bulamaz");

        // Alfabe sırası: Türkçe alfabeye göre (C < Ç, S < Ş, I < İ, O < Ö, U < Ü)
        var order = new[] { "Zil", "Üç", "Ut", "Şal", "Sal", "İş", "Isı", "Çam", "Cam", "Öz", "Ot", "Yol", "Gül", "Ağ" };
        var orgOrder = (await Rows.CreateAsync("organizations", null, Ct))["id"];
        var categoryOrder = (await Rows.CreateAsync("categories", new Dictionary<string, object?> { ["organization_id"] = orgOrder }, Ct))["id"];
        foreach (var name in order)
        {
            await Rows.CreateAsync("products", new Dictionary<string, object?> { ["organization_id"] = orgOrder, ["category_id"] = categoryOrder, ["name"] = name }, Ct);
        }

        var sorted = await Lab.ScalarAsync<string>($"SELECT STRING_AGG(name, N',') WITHIN GROUP (ORDER BY name) FROM products WHERE organization_id = '{orgOrder}'", ct: Ct);
        Assert.Equal("Ağ,Cam,Çam,Gül,Isı,İş,Ot,Öz,Sal,Şal,Ut,Üç,Yol,Zil", sorted);

        // Aynı ifadede farklı collation'lı iki sütun karşılaştırılırsa SQL Server hata verir (EF'in yazacağı p.Name == c.Name sorgusu çalışma zamanında patlar)
        var conflict = await Assert.ThrowsAsync<SqlException>(() => Lab.ExecuteAsync("SELECT COUNT(*) FROM products p JOIN categories c ON p.name = c.name", ct: Ct));
        Assert.Equal(468, conflict.Number);
        await Lab.ExecuteAsync("SELECT COUNT(*) FROM products p JOIN categories c ON p.name = c.name COLLATE DATABASE_DEFAULT", ct: Ct);

        // EF arama: LIKE ürün adı sütununun Türkçe collation'ıyla çalışır (SQL ile aynı sonuç)
        await using var db = MigrationLab.OpenDb(Lab.AppCs);
        Assert.Contains(kina, await db.Products.IgnoreQueryFilters().Where(p => p.Name.Contains("KINA")).Select(p => p.Id).ToListAsync(Ct));
        Assert.DoesNotContain(kina, await db.Products.IgnoreQueryFilters().Where(p => p.Name.Contains("kina")).Select(p => p.Id).ToListAsync(Ct));

        // Diğer metin alanları varsayılan (Latin1 CI) collation'dadır: kod/slug büyük-küçük harf farkıyla çoğaltılamaz
        var code = $"BD-{Guid.NewGuid().ToString("N")[..6]}";
        await Rows.CreateAsync("products", new Dictionary<string, object?> { ["organization_id"] = org, ["category_id"] = category, ["code"] = code }, Ct);
        await Assert.ThrowsAsync<SqlException>(() => Rows.CreateAsync("products", new Dictionary<string, object?> { ["organization_id"] = org, ["category_id"] = category, ["code"] = code.ToLowerInvariant() }, Ct));
    }

    // ------------------------------------------------------------------ NULL benzersizlik davranışı ve filtreli indeks iş kuralları

    [Fact]
    public async Task SQL_Server_benzersiz_indekste_NULL_degerleri_birbirine_esit_sayar_filtreli_indeks_bunu_kullanir()
    {
        _ = Lab;

        // PostgreSQL'den farklı: filtresiz benzersiz indekste yalnızca BİR NULL olabilir (NULL = NULL). Geçişte kuralların anlamı değişebilirdi.
        var table = $"m33_null_{Guid.NewGuid().ToString("N")[..6]}";
        await Lab.ExecuteAsync($"CREATE TABLE dbo.{table} (id int IDENTITY PRIMARY KEY, a int NULL, b int NULL); CREATE UNIQUE INDEX ux_plain ON dbo.{table} (a); CREATE UNIQUE INDEX ux_filtered ON dbo.{table} (b) WHERE b IS NOT NULL; CREATE UNIQUE INDEX ux_pair ON dbo.{table} (a, b);", ct: Ct);
        try
        {
            await Lab.ExecuteAsync($"INSERT INTO dbo.{table} (a, b) VALUES (NULL, NULL)", ct: Ct);
            await RejectedAsync($"INSERT INTO dbo.{table} (a, b) VALUES (NULL, NULL)", 2601); // ikinci NULL: hem ux_plain hem ux_pair
            await Lab.ExecuteAsync($"INSERT INTO dbo.{table} (a, b) VALUES (1, NULL)", ct: Ct); // filtreli indeks NULL'ları görmez: çoklu NULL serbest
            await Lab.ExecuteAsync($"INSERT INTO dbo.{table} (a, b) VALUES (2, NULL)", ct: Ct);
            await RejectedAsync($"INSERT INTO dbo.{table} (a, b) VALUES (3, 5), (4, 5)", 2601);
            await RejectedAsync($"INSERT INTO dbo.{table} (a, b) VALUES (NULL, 9)", 2601); // (NULL, 9) ux_plain'de a=NULL ile çakışır
        }
        finally
        {
            await Lab.ExecuteAsync($"DROP TABLE dbo.{table}", ct: Ct);
        }

        // Gerçek şemadaki NULL'lanabilir anahtarlı beş benzersiz indeks ve davranışları
        // 1) ux_product_images_ai_reference (product_id, variant_id?, angle) WHERE is_ai_reference = 1: ürün düzeyi referans (variant NULL) açı başına tek
        var product = (await Rows.CreateAsync("products", null, Ct))["id"];
        var angles = Rows.EnumLiterals("product_images", "angle");
        Assert.True(angles.Count >= 2);
        Task<Dictionary<string, object?>> Image(object? variant, string angle, bool reference) => Rows.CreateAsync("product_images",
            new Dictionary<string, object?> { ["product_id"] = product, ["variant_id"] = variant, ["angle"] = angle, ["is_ai_reference"] = reference }, Ct);
        await Image(null, angles[0], reference: true);
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => Image(null, angles[0], reference: true)); // NULL = NULL: ikinci ürün düzeyi referans reddedilir
        Assert.Equal(2601, duplicate.Number);
        Assert.Contains("ux_product_images_ai_reference", duplicate.Message, StringComparison.Ordinal);
        await Image(null, angles[1], reference: true); // başka açı serbest
        await Image(null, angles[0], reference: false); // referans olmayan görsel çoğalabilir (filtre dışı)
        await Image(null, angles[0], reference: false);
        var variant = (await Rows.CreateAsync("product_variants", new Dictionary<string, object?> { ["product_id"] = product }, Ct))["id"];
        await Image(variant, angles[0], reference: true); // varyant düzeyi referans ayrı anahtar
        await Assert.ThrowsAsync<SqlException>(() => Image(variant, angles[0], reference: true));
        var plain = await Image(null, angles[0], reference: false);
        await RejectedAsync($"UPDATE product_images SET is_ai_reference = 1 WHERE id = {Literal(plain["id"])}", 2601); // sonradan referans yapmak da çakışır

        // 2) ux_display_devices_current_session (current_session_id?) filtreli: boş cihazlar çoğalır, aynı oturuma ikinci cihaz bağlanamaz
        var sessionRow = await Rows.CreateAsync("tryon_sessions", null, Ct);
        var session = sessionRow["id"];
        Task<Dictionary<string, object?>> Device(object? current) => Rows.CreateAsync("display_devices",
            new Dictionary<string, object?> { ["store_id"] = sessionRow["store_id"], ["organization_id"] = sessionRow["organization_id"], ["current_session_id"] = current }, Ct); // TV, oturumuyla aynı şube/firmada
        await Device(null);
        await Device(null);
        await Device(session);
        Assert.Equal(2601, (await Assert.ThrowsAsync<SqlException>(() => Device(session))).Number);

        // 3) ux_ai_usage_logs_event_key (event_key?) filtreli: anahtarsız satırlar çoğalır, aynı anahtar ikinci kez yazılamaz
        await Rows.CreateAsync("ai_usage_logs", null, Ct);
        await Rows.CreateAsync("ai_usage_logs", null, Ct);
        var key = $"uncertain:{Guid.NewGuid()}";
        await Rows.CreateAsync("ai_usage_logs", new Dictionary<string, object?> { ["event_key"] = key }, Ct);
        Assert.Equal(2601, (await Assert.ThrowsAsync<SqlException>(() => Rows.CreateAsync("ai_usage_logs", new Dictionary<string, object?> { ["event_key"] = key }, Ct))).Number);

        // 4) UserNameIndex (normalized_user_name?) filtreli + collation CI: boş adlar çoğalır, büyük/küçük harf farkı çoğaltmaz (Identity zaten büyük harfe normalleştirir)
        await Rows.CreateAsync("users", null, Ct);
        await Rows.CreateAsync("users", null, Ct);
        var name = $"KULLANICI-{Guid.NewGuid().ToString("N")[..6]}";
        await Rows.CreateAsync("users", new Dictionary<string, object?> { ["normalized_user_name"] = name }, Ct);
        Assert.Equal(2601, (await Assert.ThrowsAsync<SqlException>(() => Rows.CreateAsync("users", new Dictionary<string, object?> { ["normalized_user_name"] = name.ToLowerInvariant() }, Ct))).Number);

        // 5) ux_generation_jobs_active_dedupe (session_id, dedupe_key?) filtreli: anahtarsız (varyasyon/mock) işler çoğalır
        var jobSession = await Rows.CreateAsync("tryon_sessions", null, Ct);
        for (var i = 0; i < 3; i++)
        {
            await Rows.CreateAsync("generation_jobs", new Dictionary<string, object?> { ["session_id"] = jobSession["id"], ["status"] = "Queued", ["dedupe_key"] = null }, Ct);
        }
    }

    [Fact]
    public async Task Filtreli_indeksler_is_kurallarini_korur_etkin_is_tekil_basarisiz_is_yeniden_denemeyi_engellemez_tek_varsayilan_varyant()
    {
        _ = Lab;

        // --- ux_generation_jobs_active_dedupe: aynı oturum + aynı ücretli istek için yalnızca bir etkin (Queued/Running/Succeeded) iş
        var session = await Rows.CreateAsync("tryon_sessions", null, Ct);
        var key = Guid.NewGuid().ToString("N")[..16];
        Task<Dictionary<string, object?>> Job(string status, string? dedupe = null) => Rows.CreateAsync("generation_jobs",
            new Dictionary<string, object?> { ["session_id"] = session["id"], ["organization_id"] = session["organization_id"], ["store_id"] = session["store_id"], ["status"] = status, ["dedupe_key"] = dedupe ?? key }, Ct);

        foreach (var active in new[] { "Queued", "Running", "Succeeded" })
        {
            var first = await Job(active);
            foreach (var other in new[] { "Queued", "Running", "Succeeded" })
            {
                var blocked = await Assert.ThrowsAsync<SqlException>(() => Job(other));
                Assert.Equal(2601, blocked.Number);
                Assert.Contains("ux_generation_jobs_active_dedupe", blocked.Message, StringComparison.Ordinal);
            }

            // Başarısız/iptal iş indeks dışındadır: istenildiği kadar bulunabilir ve yeniden deneme (yeni etkin iş) mümkündür
            await Job("Failed");
            await Job("Cancelled");
            await Job("Failed");
            await Lab.ExecuteAsync($"UPDATE generation_jobs SET status = N'Failed' WHERE id = {Literal(first["id"])}", ct: Ct); // etkin iş bitti → yenisi açılabilir
            var retry = await Job("Queued");
            await Lab.ExecuteAsync($"DELETE FROM generation_jobs WHERE dedupe_key = N'{key}'", ct: Ct);
            Assert.NotNull(retry);
        }

        // Durum geçişi de korunur: başarısız eski iş, etkin yeni iş varken yeniden "Queued" yapılamaz
        var stale = await Job("Failed");
        var current = await Job("Queued");
        await RejectedAsync($"UPDATE generation_jobs SET status = N'Queued' WHERE id = {Literal(stale["id"])}", 2601);
        await Lab.ExecuteAsync($"UPDATE generation_jobs SET status = N'Cancelled' WHERE id = {Literal(current["id"])}", ct: Ct);
        await Lab.ExecuteAsync($"UPDATE generation_jobs SET status = N'Queued' WHERE id = {Literal(stale["id"])}", ct: Ct);

        // Başka oturumda aynı anahtar serbest (anahtar oturum başına)
        var other2 = await Rows.CreateAsync("tryon_sessions", null, Ct);
        await Rows.CreateAsync("generation_jobs", new Dictionary<string, object?> { ["session_id"] = other2["id"], ["status"] = "Queued", ["dedupe_key"] = key }, Ct);

        // --- ux_product_variants_default: üründe tek varsayılan varyant; değiştirme tek deyimde yapılabilir
        var product = (await Rows.CreateAsync("products", null, Ct))["id"];
        Task<Dictionary<string, object?>> Variant(bool isDefault) => Rows.CreateAsync("product_variants", new Dictionary<string, object?> { ["product_id"] = product, ["is_default"] = isDefault }, Ct);
        var first2 = await Variant(true);
        Assert.Equal(2601, (await Assert.ThrowsAsync<SqlException>(() => Variant(true))).Number);
        var second = await Variant(false);
        await Variant(false);
        await RejectedAsync($"UPDATE product_variants SET is_default = 1 WHERE id = {Literal(second["id"])}", 2601); // önce eskisi kapatılmadan
        await Lab.ExecuteAsync($"UPDATE product_variants SET is_default = CASE WHEN id = {Literal(second["id"])} THEN 1 ELSE 0 END WHERE id IN ({Literal(first2["id"])}, {Literal(second["id"])})", ct: Ct); // tek deyimde takas
        Assert.Equal(1, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM product_variants WHERE product_id = '{product}' AND is_default = 1", ct: Ct));

        // --- Kuyruk/kilit indeksleri filtreli ve KUYRUK DEYİMLERİ SABİT değerle yazılmalı: parametreli durum filtreli indeksi kullanamaz
        var filters = await Lab.ScalarAsync<string>("SELECT STRING_AGG(name + N'=' + filter_definition, N';') WITHIN GROUP (ORDER BY name) FROM sys.indexes WHERE name IN (N'ix_generation_jobs_queue', N'ix_generation_jobs_running_lease')", ct: Ct);
        var byName = filters!.Split(';').ToDictionary(x => x.Split('=', 2)[0], x => SchemaInspector.Tokens(x.Split('=', 2)[1]));
        Assert.Equal("id:status lit:Queued", byName["ix_generation_jobs_queue"]);
        Assert.Equal("id:status lit:Running", byName["ix_generation_jobs_running_lease"]);
    }

    // ------------------------------------------------------------------ CHECK kısıtları: gerçek ihlal denemeleri

    [Fact]
    public async Task Sayisal_ve_iliskisel_CHECK_kisitlari_ihlalde_veritabaninda_reddedilir()
    {
        _ = Lab;

        var job = await Rows.CreateAsync("generation_jobs", new Dictionary<string, object?> { ["max_attempts"] = 3 }, Ct);
        await RejectedAsync($"UPDATE generation_jobs SET attempt = 4 WHERE id = {Literal(job["id"])}", 547); // ck_generation_jobs_attempts: attempt <= max_attempts
        await RejectedAsync($"UPDATE generation_jobs SET attempt = -1 WHERE id = {Literal(job["id"])}", 547);
        await Lab.ExecuteAsync($"UPDATE generation_jobs SET attempt = 3 WHERE id = {Literal(job["id"])}", ct: Ct);
        await RejectedAsync($"UPDATE generation_jobs SET max_attempts = 2 WHERE id = {Literal(job["id"])}", 547); // alt sınıra düşürmek de ihlal

        var category = await Rows.CreateAsync("categories", null, Ct);
        await RejectedAsync($"UPDATE categories SET max_selection = 0 WHERE id = {Literal(category["id"])}", 547); // ck_categories_max_selection

        var product = (await Rows.CreateAsync("products", null, Ct))["id"];
        var selfComponent = await Assert.ThrowsAsync<SqlException>(() => Rows.CreateAsync("outfit_set_components", new Dictionary<string, object?> { ["set_product_id"] = product, ["component_product_id"] = product }, Ct));
        Assert.Contains("ck_outfit_set_components_not_self", selfComponent.Message, StringComparison.Ordinal); // takım kendi bileşeni olamaz

        var log = await Rows.CreateAsync("ai_usage_logs", new Dictionary<string, object?> { ["event_key"] = $"uncertain:{Guid.NewGuid()}", ["reconciliation_state"] = "Open", ["error_code"] = "OutcomeUncertain" }, Ct);
        await RejectedAsync($"UPDATE ai_usage_logs SET is_adjustment = 1 WHERE id = {Literal(log["id"])}", 547); // düzeltme satırı asıl kayıt göstermeli (adjusts_log_id)
        await RejectedAsync($"UPDATE ai_usage_logs SET reconciliation_state = N'Bilinmiyor' WHERE id = {Literal(log["id"])}", 547);

        var decision = new Dictionary<string, object?> { ["usage_log_id"] = log["id"], ["decision"] = "ConfirmedCharged", ["confirmed_amount_usd"] = 0.1m, ["effective_cost_usd"] = 0.1m };
        await Rows.CreateAsync("ai_cost_reconciliations", decision, Ct);
        foreach (var (column, value, constraint) in new (string, object, string)[]
                 {
                     ("sequence", 0, "ck_ai_cost_reconciliations_sequence"), ("effective_cost_usd", -0.01m, "ck_ai_cost_reconciliations_amount"),
                     ("confirmed_amount_usd", 0m, "ck_ai_cost_reconciliations_amount"), ("decision", "Open", "ck_ai_cost_reconciliations_decision"),
                 })
        {
            var overrides = new Dictionary<string, object?>(decision) { ["sequence"] = 2, ["idempotency_key"] = Guid.NewGuid().ToString("N"), [column] = value };
            var ex = await Assert.ThrowsAsync<SqlException>(() => Rows.CreateAsync("ai_cost_reconciliations", overrides, Ct));
            Assert.Equal(547, ex.Number);
            Assert.Contains(constraint, ex.Message, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ maliyet ve mutabakat tutarlılığı

    /// <summary>Değişmezler firma bazında sınanır ({0} = firma kimliği): ortak laboratuvardaki diğer testlerin yapay satırları sonucu etkilemesin.</summary>
    internal static readonly (string Name, string Sql)[] CostInvariants =
    [
        ("her karar için tam bir düzeltme satırı var", "SELECT COUNT(*) FROM ai_cost_reconciliations d WHERE d.organization_id = '{0}' AND (SELECT COUNT(*) FROM ai_usage_logs a WHERE a.is_adjustment = 1 AND a.adjusts_log_id = d.usage_log_id AND a.event_key = CONCAT(N'reconcile:', LOWER(CONVERT(nvarchar(36), d.id)))) <> 1"),
        ("sahipsiz düzeltme satırı yok", "SELECT COUNT(*) FROM ai_usage_logs a WHERE a.organization_id = '{0}' AND a.is_adjustment = 1 AND NOT EXISTS (SELECT 1 FROM ai_cost_reconciliations d WHERE d.usage_log_id = a.adjusts_log_id AND a.event_key = CONCAT(N'reconcile:', LOWER(CONVERT(nvarchar(36), d.id))))"),
        ("ilk tutar + düzeltmeler = son kararın efektif maliyeti", "SELECT COUNT(*) FROM ai_usage_logs c CROSS APPLY (SELECT TOP 1 d.effective_cost_usd AS e FROM ai_cost_reconciliations d WHERE d.usage_log_id = c.id ORDER BY d.sequence DESC) last WHERE c.organization_id = '{0}' AND c.estimated_cost_usd + ISNULL((SELECT SUM(a.estimated_cost_usd) FROM ai_usage_logs a WHERE a.adjusts_log_id = c.id), 0) <> last.e"),
        ("vaka durumu = son karar (karar yoksa Open)", "SELECT COUNT(*) FROM ai_usage_logs c WHERE c.organization_id = '{0}' AND c.is_adjustment = 0 AND c.reconciliation_state IS NOT NULL AND c.reconciliation_state <> COALESCE((SELECT TOP 1 d.decision FROM ai_cost_reconciliations d WHERE d.usage_log_id = c.id ORDER BY d.sequence DESC), N'Open')"),
        ("karar sıraları 1..n boşluksuz", "SELECT COUNT(*) FROM (SELECT usage_log_id, COUNT(*) AS n, MAX(sequence) AS m FROM ai_cost_reconciliations WHERE organization_id = '{0}' GROUP BY usage_log_id) x WHERE x.n <> x.m"),
        ("düzeltme satırı vakayla aynı firma/şube/sağlayıcı/model/muhasebe tarihi", "SELECT COUNT(*) FROM ai_usage_logs a JOIN ai_usage_logs c ON c.id = a.adjusts_log_id WHERE a.organization_id = '{0}' AND a.is_adjustment = 1 AND (a.organization_id <> c.organization_id OR a.store_id <> c.store_id OR a.provider <> c.provider OR a.model <> c.model OR a.occurred_at IS NULL OR a.occurred_at <> COALESCE(c.occurred_at, c.created_at))"),
        ("karar vakayla aynı firmaya ait", "SELECT COUNT(*) FROM ai_cost_reconciliations d JOIN ai_usage_logs c ON c.id = d.usage_log_id WHERE c.organization_id = '{0}' AND d.organization_id <> c.organization_id"),
        ("belirsiz her iş için maliyet satırı var", "SELECT COUNT(*) FROM generation_jobs j WHERE j.organization_id = '{0}' AND j.status = N'Failed' AND j.error_code = N'OutcomeUncertain' AND NOT EXISTS (SELECT 1 FROM ai_usage_logs u WHERE u.job_id = j.id AND u.error_code = N'OutcomeUncertain' AND u.is_adjustment = 0)"),
        ("iş başına en fazla bir belirsiz maliyet vakası", "SELECT COUNT(*) FROM (SELECT job_id FROM ai_usage_logs WHERE organization_id = '{0}' AND is_adjustment = 0 AND reconciliation_state IS NOT NULL AND job_id IS NOT NULL GROUP BY job_id HAVING COUNT(*) > 1) x"),
    ];

    [Fact]
    public async Task Maliyet_ve_mutabakat_degismezleri_esit_zamanli_kararlar_supurucu_ve_tekrarlar_sonrasinda_korunur()
    {
        _ = Lab;
        var org = (await Rows.CreateAsync("organizations", null, Ct))["id"];
        var store = (await Rows.CreateAsync("stores", new Dictionary<string, object?> { ["organization_id"] = org }, Ct))["id"];
        var cases = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var row = await Rows.CreateAsync("ai_usage_logs", new Dictionary<string, object?>
            {
                ["organization_id"] = org, ["store_id"] = store, ["estimated_cost_usd"] = 0.15m, ["error_code"] = "OutcomeUncertain", ["success"] = false,
                ["event_key"] = $"uncertain:{Guid.NewGuid()}", ["reconciliation_state"] = "Open", ["provider"] = "Fashn", ["model"] = "tryon-max",
            }, Ct);
            cases.Add((Guid)row["id"]!);
        }

        // Maliyet satırı olmayan belirsiz işler: süpürücü tamamlar
        for (var i = 0; i < 3; i++)
        {
            await Rows.CreateAsync("generation_jobs", new Dictionary<string, object?> { ["organization_id"] = org, ["store_id"] = store, ["status"] = "Failed", ["error_code"] = "OutcomeUncertain", ["provider"] = "Fashn", ["model"] = "tryon-max" }, Ct);
        }

        var sweep = new CostReconciliationSweep(null!, new FixedOptions<GenerationSettings>(new GenerationSettings()), TimeProvider.System, NullLogger<CostReconciliationSweep>.Instance);
        var service = new CostReconciliationService(TimeProvider.System);
        var recorded = 0;
        var rng = new Random(33);
        var amounts = new[] { 0.05m, 0.1m, 0.15m, 0.2m };

        async Task Admin(Guid caseId, int seed)
        {
            var local = new Random(seed);
            for (var round = 0; round < 6; round++)
            {
                await using var db = MigrationLab.OpenDb(Lab.AppCs);
                var expected = await db.AiCostReconciliations.IgnoreQueryFilters().CountAsync(d => d.UsageLogId == caseId, Ct);
                var decision = (CostDecision)local.Next(3);
                var command = new DecideCommand(decision, decision == CostDecision.ConfirmedCharged ? amounts[local.Next(amounts.Length)] : null, null, expected);
                var key = Guid.NewGuid().ToString("N");
                var result = await service.DecideAsync(db, caseId, Guid.NewGuid(), key, command, null, Ct);
                if (result.Outcome == DecisionOutcome.Recorded)
                {
                    Interlocked.Increment(ref recorded);
                    await using var again = MigrationLab.OpenDb(Lab.AppCs); // aynı istek tekrarı: ikinci kayıt yok
                    Assert.Equal(DecisionOutcome.Replayed, (await service.DecideAsync(again, caseId, Guid.NewGuid(), key, command, null, Ct)).Outcome);
                }
                else
                {
                    Assert.Contains(result.Outcome, new[] { DecisionOutcome.SequenceMismatch, DecisionOutcome.Replayed });
                }
            }
        }

        async Task Sweep()
        {
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    await using var db = MigrationLab.OpenDb(Lab.AppCs);
                    await sweep.RunOnceAsync(db, Ct);
                }
                catch (SqlException ex) when (ex.Number is 2601 or 1205)
                {
                    // Eşzamanlı süpürücüler aynı satırı yazmaya çalıştı: benzersiz indeks/kilit kazananı belirledi; üretimde döngü sonraki turda yeniden dener
                }
            }
        }

        var tasks = cases.SelectMany(c => Enumerable.Range(0, 4).Select(i => Admin(c, rng.Next() + i))).Concat(Enumerable.Range(0, 3).Select(_ => Sweep())).ToArray();
        await Task.WhenAll(tasks);

        await using (var db = MigrationLab.OpenDb(Lab.AppCs))
        {
            await sweep.RunOnceAsync(db, Ct); // kalan eksikler (yarışta kaybedilenler) sıralı turda tamamlanır
        }

        Assert.True(recorded >= cases.Count, $"Beklenenden az karar yazıldı: {recorded}");
        var violations = new List<string>();
        foreach (var (name, sql) in CostInvariants)
        {
            var count = await Lab.ScalarAsync<int>(sql.Replace("{0}", org!.ToString(), StringComparison.Ordinal), ct: Ct);
            if (count != 0)
            {
                violations.Add($"{name}: {count} ihlal");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.Equal(recorded, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_cost_reconciliations WHERE organization_id = '{org}'", ct: Ct));
        Assert.Equal(recorded, await Lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_usage_logs WHERE organization_id = '{org}' AND is_adjustment = 1", ct: Ct));
    }

    // ------------------------------------------------------------------ bulgular (veritabanının garanti etmediği şeyler)

    [Fact]
    public async Task Firma_sutunu_olan_her_tablo_firma_FK_sina_veya_bilesik_FK_ya_baglidir_firma_kimligi_dogrulanmis_olur()
    {
        _ = Lab;

        // organization_id taşıyan her tablo ya doğrudan organizations'a bağlıdır ya da organization_id'yi içeren bir bileşik FK ile firması doğrulanmış bir ebeveyne bağlıdır
        var uncovered = await Lab.ScalarAsync<string>("""
            SELECT STRING_AGG(t.name, N',') WITHIN GROUP (ORDER BY t.name)
              FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id AND c.name = N'organization_id'
             WHERE t.is_ms_shipped = 0 AND NOT EXISTS (
                    SELECT 1 FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                     WHERE fk.parent_object_id = t.object_id AND fkc.parent_column_id = c.column_id)
            """, ct: Ct);
        Assert.True(string.IsNullOrEmpty(uncovered), $"Firma sütunu hiçbir FK ile doğrulanmayan tablolar: {uncovered}");

        // Zincirin kökleri: firma sütunu olan ve organizations'a doğrudan bağlı tablolar
        var roots = await Lab.ScalarAsync<string>("""
            SELECT STRING_AGG(t.name, N',') WITHIN GROUP (ORDER BY t.name)
              FROM sys.tables t WHERE EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.parent_object_id = t.object_id AND fk.referenced_object_id = OBJECT_ID(N'dbo.organizations'))
            """, ct: Ct);
        Assert.Equal("account_setup_tokens,audit_logs,categories,organization_agreement_acceptances,products,stored_files,stores,user_store_roles,users", roots);
    }

    /// <summary>
    /// Çapraz firma/şube ilişkisi veritabanında <b>reddedilir</b>: çocuk satırın firması ebeveyninin firmasından farklıysa (veya iş/TV oturumdan farklı şubedeyse) bileşik yabancı anahtar
    /// 547 ile reddeder. Her bileşik FK için ayrı kanıt; yeni bileşik FK eklenince otomatik kapsanır.
    /// </summary>
    [Fact]
    public async Task Capraz_firma_ve_capraz_sube_iliskisi_kuran_satirlar_veritabaninda_reddedilir()
    {
        _ = Lab;
        var otherOrg = (await Rows.CreateAsync("organizations", null, Ct))["id"];
        var problems = new List<string>();
        // Bileşik tenant FK'ları katalogdan değil BEKLENEN ADLARDAN türetilir: bir FK sessizce tek sütuna düşerse (veya silinirse) liste küçülmez, test kırılır.
        // Adı "_organization_id" ile biten ve organizations'a işaret etmeyen her FK bileşik olmalıdır (kimlik + firma [+ şube]).
        var expectedTenantNames = ExpectedForeignKeys.Keys.Where(n => n.EndsWith("_organization_id", StringComparison.Ordinal) && !n.Contains("_organizations_", StringComparison.Ordinal)).ToList();
        Assert.Equal(39, expectedTenantNames.Count); // 39 bileşik tenant FK'sı (34 değiştirilen + 3 yeni eş/denetim FK'sı + 2 M5.1 FK'sı: kurulum token'ı, sözleşme kabulü)
        var notComposite = expectedTenantNames.Where(n => Rows.ForeignKeys.SingleOrDefault(f => f.Name == n) is not { } f || f.ChildColumns.Count < 2 || !f.ChildColumns.Contains("organization_id")).ToList();
        Assert.True(notComposite.Count == 0, $"Bileşik olması gereken FK'lar değil/yok: {string.Join(", ", notComposite)}");
        var tenantFks = Rows.ForeignKeys.Where(f => expectedTenantNames.Contains(f.Name) && f.Child != f.Parent).ToList();
        Assert.True(tenantFks.Count >= 33, $"Beklenenden az bileşik ilişki: {tenantFks.Count}");

        foreach (var fk in tenantFks)
        {
            // Hata, bu ebeveyne bağlanan herhangi bir bileşik tenant FK'sından gelebilir (çocuğun ebeveyn zincirindeki satırlar da aynı firmada yaratılmaya çalışılır)
            var childFkNames = Rows.ForeignKeys.Where(f => f.ChildColumns.Contains("organization_id") && (f.Parent == fk.Parent || f.Child == fk.Child)).Select(f => f.Name).ToList();
            var parent = await Rows.CreateAsync(fk.Parent, null, Ct);

            // (a) çocuk başka bir firmada
            var overrides = fk.ChildColumns.Select((c, i) => (c, parent[fk.ParentColumns[i]])).ToDictionary(p => p.c, p => p.Item2, StringComparer.Ordinal);
            overrides["organization_id"] = otherOrg;
            SqlException? ex = null;
            try
            {
                await Rows.CreateAsync(fk.Child, overrides, Ct);
            }
            catch (SqlException caught)
            {
                ex = caught;
            }

            if (ex is null)
            {
                problems.Add($"{fk.Name}: çapraz firma satırı KABUL EDİLDİ");
            }
            else if (ex.Number != 547 || !childFkNames.Any(n => ex.Message.Contains(n, StringComparison.Ordinal)))
            {
                problems.Add($"{fk.Name}: çapraz firma satırı beklenen bileşik FK ile reddedilmedi ({ex.Number}: {ex.Message})");
            }

            // (b) aynı firmada ama başka şubede (yalnızca şube sütunu da olan ilişkilerde: iş → oturum, TV → oturum)
            if (fk.ChildColumns.Contains("store_id") && Rows.Columns[fk.Parent].Any(c => c.Name == "store_id"))
            {
                var store = (await Rows.CreateAsync("stores", new Dictionary<string, object?> { ["organization_id"] = parent["organization_id"] }, Ct))["id"];
                var sameFirm = fk.ChildColumns.Select((c, i) => (c, parent[fk.ParentColumns[i]])).ToDictionary(p => p.c, p => p.Item2, StringComparer.Ordinal);
                sameFirm["store_id"] = store;
                SqlException? other = null;
                try
                {
                    await Rows.CreateAsync(fk.Child, sameFirm, Ct);
                }
                catch (SqlException caught)
                {
                    other = caught;
                }

                if (other is null)
                {
                    problems.Add($"{fk.Name}: çapraz şube satırı KABUL EDİLDİ");
                }
                else if (other.Number != 547 || !Rows.ForeignKeys.Where(f => f.Child == fk.Child).Any(f => other.Message.Contains(f.Name, StringComparison.Ordinal)))
                {
                    problems.Add($"{fk.Name}: çapraz şube satırı beklenen FK ile reddedilmedi ({other.Number}: {other.Message})");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
