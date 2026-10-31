using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SanalProva.Domain.Common;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.4: izolasyonun mimari korumaları. Bunlar veritabanı gerektirmez ve her derlemede çalışır: yeni bir tablo/yol eklendiğinde izolasyon kararının bilinçli verilmesini zorlar.
/// </summary>
public sealed class TenantArchitectureTests
{
    private static AppDbContext Model() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=tcp:127.0.0.1,1;Database=none;User ID=n;Password=n;Encrypt=False", AppDbContext.ConfigureSqlServer).UseSnakeCaseNamingConvention().Options,
        NoTenantContext.Instance);

    /// <summary>OrganizationId taşıyıp EF sorgu filtresi OLMAYAN varlıklar ve nedenleri. Başka hiçbiri olamaz.</summary>
    private static readonly Dictionary<string, string> UnfilteredWithOrganization = new(StringComparer.Ordinal)
    {
        ["AuditLog"] = "Denetim kaydı: uygulama dışından okunmaz (okuma ucu yok); platform yöneticisi başka firma adına kayıt yazar (hedef firma OrganizationId'si).",
        ["AppUser"] = "Giriş sırasında kiracı henüz bilinmez; kullanıcı e-postayla bulunur. Kullanıcı sorguları açıkça firma ile filtrelenir (Tenancy.cs, AuthEndpoints).",
    };

    [Fact]
    public void Firma_sutunu_olan_her_varlik_sorgu_filtresine_sahiptir_sube_varliklari_sube_kosulunu_da_icerir()
    {
        using var db = Model();
        var problems = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.FindProperty("OrganizationId") is not null))
        {
            var name = entity.ClrType.Name;
            var filter = entity.GetDeclaredQueryFilters().FirstOrDefault()?.Expression;
            var tenantOwned = typeof(ITenantOwned).IsAssignableFrom(entity.ClrType);
            var storeOwned = typeof(IStoreOwned).IsAssignableFrom(entity.ClrType);

            if (filter is null)
            {
                if (!UnfilteredWithOrganization.ContainsKey(name))
                {
                    problems.Add($"{name}: OrganizationId var ama sorgu filtresi yok ve izinli listede değil (ITenantOwned uygulayın veya nedenini belgeleyin).");
                }

                continue;
            }

            var text = filter.ToString();
            if (!tenantOwned)
            {
                problems.Add($"{name}: filtre var ama ITenantOwned değil.");
            }

            if (!text.Contains("OrganizationId", StringComparison.Ordinal))
            {
                problems.Add($"{name}: filtre firma koşulunu içermiyor: {text}");
            }

            if (storeOwned && !text.Contains("StoreId", StringComparison.Ordinal))
            {
                problems.Add($"{name}: IStoreOwned ama filtre şube koşulunu içermiyor: {text}");
            }
        }

        // Oturuma/seçime bağlı kayıtlarda şube kapsamı YAPISAL: filtre ebeveyn kümesini (oturum/seçim) içerir; elle birleştirmeye bağımlı değildir
        foreach (var entity in db.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            var text = entity.GetDeclaredQueryFilters().FirstOrDefault()?.Expression?.ToString() ?? string.Empty;
            if (typeof(ISessionOwned).IsAssignableFrom(entity.ClrType) && !text.Contains(".Any(p => (p.Id == e.SessionId))", StringComparison.Ordinal))
            {
                problems.Add($"{entity.ClrType.Name}: ISessionOwned ama filtre oturum alt sorgusunu (Any(p => p.Id == e.SessionId)) içermiyor: {text}");
            }

            if (typeof(ISelectionOwned).IsAssignableFrom(entity.ClrType) && !text.Contains(".Any(p => (p.Id == e.SelectionId))", StringComparison.Ordinal))
            {
                problems.Add($"{entity.ClrType.Name}: ISelectionOwned ama filtre seçim alt sorgusunu (Any(p => p.Id == e.SelectionId)) içermiyor: {text}");
            }
        }

        // İzinli listedeki varlıklar hâlâ gerçekten filtresiz mi (liste bayatlamasın)?
        foreach (var name in UnfilteredWithOrganization.Keys)
        {
            var entity = db.Model.GetEntityTypes().Single(e => e.ClrType.Name == name);
            if (entity.GetDeclaredQueryFilters().Count != 0)
            {
                problems.Add($"{name}: artık filtreli; izinli listeden çıkarın.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ------------------------------------------------------------------ filtre atlama ve ham SQL envanteri

    /// <summary>
    /// Tenant filtresini atlayan veya ham SQL/toplu yazma yapan her kullanım <b>bilinçli incelenmiş</b> olmalıdır. Yeni kullanım bu testi kırar: gerekçe yazılıp envantere eklenmeden birleştirilemez.
    /// (Ham SQL'de firma/şube koşulu elle yazılmak zorundadır; EF filtresi uygulanmaz.)
    /// </summary>
    private static readonly Dictionary<string, (int IgnoreFilters, int RawSql, int BulkWrite, string Reason)> Inventory = new(StringComparer.Ordinal)
    {
        // --- Arka plan işleyicileri: firmalar arası çalışmak zorundadır; her iş için kiracı bağlamı işin firmasına ayarlanır, girdiler (fotoğraf, seçim, ürün, dosya) FİLTRELİ sorgularla yüklenir
        ["SanalProva.Infrastructure/Generation/GenerationProcessor.cs"] = (11, 2, 4, "Kuyruk (iş kimliğiyle, firma bağlamı işten ayarlanır), kilit yenileme/kurtarma ve kurtarmada atomik maliyet kaydı (SQL'de job.organization_id taşınır). Girdi yükleme filtrelidir. İş silinmiş mi denetimi (KVKK silmesiyle yarış) yalnızca iş kimliğiyle."),
        ["SanalProva.Infrastructure/Credits/CreditLedgerService.cs"] = (12, 0, 0, "Kredi defteri servisi (M5.4): işçi, süpürücü, iptal ucu ve operatör komutu firma bağlamından bağımsız çağırır; her sorgu açıkça firma veya iş kimliğine bağlıdır, yazımlar firma başına uygulama kilidi altındadır."),
        ["SanalProva.Api/Infrastructure/ScreenLock.cs"] = (0, 0, 1, "Pilot kapanış: kilitli sayılacak oturumu İSTEMCİ sinyali kaçırdıysa sunucu kendi oturum satırını (yalnızca çağıranın sp_sid kimliği) koşullu kilitler; başka satıra/firmaya dokunmaz."),
        ["SanalProva.Infrastructure/Monitoring/PilotMonitoring.cs"] = (3, 0, 0, "Faz C: pilot izleme (salt okunur): platform yöneticisi ekranı ve operatör komutu için firmalar arası özet (kredi hesapları, belirsiz maliyet sayıları, takılı işler). Hiçbir yazma yok; kişisel veri döndürmez (yalnızca sayılar/firma adı); yalnızca RequirePlatformAdmin uçları ve `platform status` CLI'sinden çağrılır."),
        ["SanalProva.Api/Features/Account/PinEndpoints.cs"] = (0, 0, 5, "Faz C + pilot kapanış: PIN hatalı deneme sayacı ve kilit/aktivite/açma güncellemeleri yalnızca çağıranın KENDİ kullanıcı satırı ve KENDİ oturum satırı (sp_sid) üzerindedir (Where Id == oturumdaki kimlik); firma/şube verisine dokunmaz."),
        ["SanalProva.Infrastructure/Generation/CostReconciliation.cs"] = (5, 1, 0, "Süpürücü ve mutabakat servisi: firmalar arası bakım (M5.3: tüm ham SQL tek yardımcıdan geçer: eşzamanlı süpürücüde benzersiz indeks ihlali yutulur). Yalnızca platform yöneticisi uç noktasından ve süpürücüden çağrılır; kayıtlar kendi firma/şubelerini taşır."),
        ["SanalProva.Infrastructure/Generation/BudgetGuard.cs"] = (5, 0, 1, "Bütçe toplamı ve açık rezervasyonlar: sorgular açıkça organization_id ile sınırlı; rezervasyon yazımı iş kimliği + deneme numarası + Running koşuluyla."),
        ["SanalProva.Infrastructure/Persistence/AppLocks.cs"] = (0, 1, 0, "sp_getapplock: veri değil kilit; kaynak adı şube/oturum kimliğidir."),
        ["SanalProva.Infrastructure/Persistence/AppDbContext.cs"] = (1, 0, 0, "Yalnızca belge yorumu (\"IgnoreQueryFilters()\" ile aşılır): kod kullanımı değil."),
        ["SanalProva.Infrastructure/Identity/AccountSetupTokenService.cs"] = (4, 2, 0, "M5.1b: kurulum token'ları hem platform yöneticisinin (hedef firma kendi bağlamında değil) hem anonim tüketicinin (kiracı bağlamı hiç yok) işlemesi gerekir; satırlar token_hash/kullanıcı+amaç ile aranır, firma sütunu yalnızca FK tutarlılığı içindir. (1 kullanım belge yorumunda.) Tüketim/iptal atomik koşullu UPDATE (ham SQL) ile yapılır."),
        // --- Saklama / KVKK: oturum kimliğiyle silme (süpürücü firmalar arası çalışır)
        ["SanalProva.Infrastructure/Retention/SessionRetention.cs"] = (11, 0, 3, "KVKK silmesi, saklama süpürmesi ve yetim dosya/kayıt süpürücüsü: oturum/dosya kimliğiyle (firmalar arası bakım); zincirleme silme veritabanı FK'larındadır."),
        // --- TV: cihaz belirteci kimliği; cihaz kendi şubesine/oturumuna bağlıdır
        ["SanalProva.Infrastructure/Displays/DisplayStateService.cs"] = (6, 0, 1, "TV durumu tek yazma noktası: cihaz kimliğiyle; görsel yalnızca cihazın bağlı oturumuna aitse gösterilir. Süresi dolan eşleştirme temizliği (firmasız tablo)."),
        ["SanalProva.Api/Hubs/Hubs.cs"] = (13, 0, 2, "SignalR: kimlik doğrulanmış bağlantının oturum/şube rolü ve şubenin etkinliği (firma kimliğiyle açıkça) sorgulanır; TV hub'ı cihaz belirtecinden gelen cihaz kimliğini kullanır."),
        ["SanalProva.Api/Features/Display/DeviceTokenAuthentication.cs"] = (1, 0, 0, "Cihaz belirteci özeti → cihaz (kiracı henüz bilinmez)."),
        ["SanalProva.Api/Features/Display/DisplayEndpoints.cs"] = (1, 0, 1, "Cihaz belirteci kontrolü; eşleştirme talebi (firmasız bekleyen kayıt) yönetici kimliğiyle."),
        // --- Kimlik/yetki: kiracı bağlamı kurulmadan önce
        ["SanalProva.Api/Infrastructure/Tenancy.cs"] = (3, 0, 0, "Şube/firma rol filtreleri: kullanıcı, şube ve firma kimliğiyle açıkça sorgular (bağlam bu filtrelerle kurulur)."),
        ["SanalProva.Api/Features/Auth/AuthEndpoints.cs"] = (2, 0, 2, "Giriş sonrası kullanıcının şube listesi: kullanıcı kimliği ve firmasıyla. Çıkışta ve girişte yalnızca çağıranın kendi oturum satırları (auth_sessions) silinir."),
        // --- Platform yöneticisi (yalnızca PlatformAdminFilter arkasında; her karar denetim kaydı bırakır)
        ["SanalProva.Api/Features/Agreement/AgreementGate.cs"] = (1, 0, 0, "M5.1e: sözleşme kapısı her kimliklendirilmiş istekte firma kimliğiyle (claim) açıkça sorgular; kiracı bağlamı henüz kurulmadan/filtrelerden bağımsız çalışması gerekir. Yalnızca varlık sorgusu (AnyAsync), veri dönmez; olumlu sonuç önbelleğe alınır."),
        ["SanalProva.Api/Features/Agreement/AgreementEndpoints.cs"] = (2, 0, 0, "M5.1e: sözleşme kabulü/görüntüleme: firma kimliği ve kullanıcı kimliği açıkça koşulda (Owner rol denetimi, kabul zamanı); yalnızca çağıranın kendi firması."),
        ["SanalProva.Api/Features/Platform/PlatformOnboardingService.cs"] = (4, 0, 0, "M5.1c: platform yöneticisi başka firma adına firma/şube/Owner oluşturur ve idempotency tekrarında yeni firmanın şube/rolünü okur: hedef firma çağıranın bağlamı değildir. Yalnızca PlatformAdminFilter arkasından çağrılır; okumalar firma kimliğiyle açıkça sınırlı, her işlem hedef firmaya denetim kaydı bırakır."),
        ["SanalProva.Api/Features/Platform/PlatformCommands.cs"] = (2, 0, 0, "M5.4: operatör komutu (credits) hesap bakiyesini firma slug'ıyla okur; yazım CreditLedgerService'tedir. Faz A: reapply-deletions silme günlüğündeki oturum kimliklerinin (firma bağlamı olmayan CLI) geri yüklenen veritabanında yeniden belirip belirmediğini yalnızca kimlikle okur; silme SessionPurger'dadır. Web sunucusu başlamaz, HTTP'den çağrılamaz."),
        ["SanalProva.Api/Features/Platform/PlatformOrganizationEndpoints.cs"] = (6, 0, 0, "M5.1c/h: platform yöneticisi okuma uçları (liste/ayrıntı + sözleşme kabul durumu): firma kimliğiyle sınırlı, yalnızca özet bilgi (oturum/fotoğraf/katalog verisi dönmez); PlatformAdminFilter arkasında."),
        ["SanalProva.Api/Features/Admin/PlatformCostEndpoints.cs"] = (7, 0, 0, "Platform yöneticisi: bilerek firmalar arası okuma; yetki her istekte veritabanından doğrulanır."),
        // --- Sıradan uç noktalar: filtre atlamaz; toplu yazma FİLTRELİ DbSet üzerindendir (EF filtresi ExecuteUpdate/ExecuteDelete'e de uygulanır)
        ["SanalProva.Api/Features/Generation/GenerationEndpoints.cs"] = (0, 0, 1, "İş iptali: filtreli DbSet (firma+şube) üzerinde koşullu UPDATE."),
        ["SanalProva.Api/Features/TryOn/SessionEndpoints.cs"] = (0, 0, 1, "Fotoğraf silinince işlerin kaynak fotoğraf bağlantısını boşaltır: filtreli DbSet."),
        ["SanalProva.Api/Features/TryOn/SessionReader.cs"] = (1, 0, 0, "Kuyruk sırası: yalnızca sayı döner (firmalar arası ortak kuyruk; içerik sızdırmaz)."),
    };

    private static IEnumerable<(string Path, int Ignore, int Raw, int Bulk)> Scan()
    {
        var root = Path.GetDirectoryName(MigrationLab.RepoFile("backend/src/SanalProva.Api/SanalProva.Api.csproj"))!;
        var src = Directory.GetParent(root)!.FullName;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var ignore = Regex.Count(text, @"IgnoreQueryFilters\(");
            var raw = Regex.Count(text, @"ExecuteSqlInterpolated|ExecuteSqlRaw|FromSql|SqlQuery\b|SqlQueryRaw");
            var bulk = Regex.Count(text, @"ExecuteUpdate|ExecuteDelete");
            if (ignore + raw + bulk > 0)
            {
                yield return (relative, ignore, raw, bulk);
            }
        }
    }

    [Fact]
    public void Filtre_atlayan_ve_ham_SQL_kullanimlari_onayli_envanterle_sinirlidir()
    {
        var actual = Scan().ToDictionary(x => x.Path, x => (x.Ignore, x.Raw, x.Bulk), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (path, counts) in actual)
        {
            if (!Inventory.TryGetValue(path, out var approved))
            {
                problems.Add($"YENİ: {path} → atlama {counts.Ignore}, ham SQL {counts.Raw}, toplu yazma {counts.Bulk} (envanterde yok)");
            }
            else if ((approved.IgnoreFilters, approved.RawSql, approved.BulkWrite) != counts)
            {
                problems.Add($"DEĞİŞTİ: {path} → atlama {counts.Ignore}, ham SQL {counts.Raw}, toplu yazma {counts.Bulk} (onaylı: {approved.IgnoreFilters}/{approved.RawSql}/{approved.BulkWrite})");
            }
        }

        problems.AddRange(Inventory.Keys.Except(actual.Keys).Select(p => $"KALDIRILDI: {p} envanterde ama kullanım kalmamış"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
