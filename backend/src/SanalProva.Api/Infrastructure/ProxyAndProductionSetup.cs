using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.SqlClient;

namespace SanalProva.Api.Infrastructure;

/// <summary>
/// Ters vekil (Caddy) arkasında istemci IP'si ve şeması için <c>X-Forwarded-*</c> başlıkları (Pilot Faz A). <b>Yalnızca güvenilen vekillerden</b> gelen başlıklar kabul edilir:
/// <c>ForwardedHeaders:KnownProxies</c> (IP listesi) ve/veya <c>ForwardedHeaders:KnownNetworks</c> (CIDR listesi). İkisi de boşsa ara katman HİÇ eklenmez (başlıklar yok sayılır,
/// bu varsayılan davranıştır: sahte <c>X-Forwarded-For</c> ile hız sınırı/denetim IP'si taklit edilemez). Yalnızca <c>For</c> ve <c>Proto</c> işlenir
/// (<c>Host</c> başlığına güvenilmez; <c>AllowedHosts</c> gerçek Host'u denetler); <c>ForwardLimit=1</c>: yalnızca en yakın vekilin eklediği değer kullanılır.
/// Kimlik (çerez, CSRF) bu başlıklara bağlı değildir; etkisi hız sınırı kovaları, denetim IP özeti ve HSTS/şema algısıdır.
/// </summary>
public static class TrustedForwardedHeaders
{
    public const string Section = "ForwardedHeaders";

    public static bool IsConfigured(IConfiguration configuration) =>
        Read(configuration, "KnownProxies").Length > 0 || Read(configuration, "KnownNetworks").Length > 0;

    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var proxies = Read(configuration, "KnownProxies");
        var networks = Read(configuration, "KnownNetworks");
        if (proxies.Length == 0 && networks.Length == 0)
        {
            return services;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = false;
            // Varsayılan güvenilenler (loopback) temizlenir: yalnızca yapılandırılanlar
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        return services;
    }

    /// <summary>Yapılandırılmışsa ara katmanı ekler (boru hattının EN BAŞINDA çağrılmalı).</summary>
    public static IApplicationBuilder UseTrustedForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        return IsConfigured(configuration) ? app.UseForwardedHeaders() : app;
    }

    /// <summary>Geçersiz IP/CIDR değerleri uygulama açılışında hata verir (sessizce yok sayılmaz).</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        var problems = new List<string>();
        foreach (var value in Read(configuration, "KnownProxies"))
        {
            if (!IPAddress.TryParse(value, out _))
            {
                problems.Add($"ForwardedHeaders:KnownProxies geçersiz IP içeriyor: '{value}'.");
            }
        }

        foreach (var value in Read(configuration, "KnownNetworks"))
        {
            if (!System.Net.IPNetwork.TryParse(value, out _))
            {
                problems.Add($"ForwardedHeaders:KnownNetworks geçersiz CIDR içeriyor: '{value}'.");
            }
        }

        return problems;
    }

    private static string[] Read(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection($"{Section}:{key}");
        var list = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (list.Count == 0 && section.Value is { Length: > 0 } csv)
        {
            list = csv.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); // ortam değişkeni: "10.0.0.0/24,172.31.250.0/24"
        }

        return [.. list];
    }
}

/// <summary>
/// Üretim (<c>ASPNETCORE_ENVIRONMENT=Production</c>) başlangıç denetimi (Pilot Faz A): eksik veya güvensiz yapılandırmayla uygulama BAŞLAMAZ. Hata iletileri yalnızca anahtar adını
/// ve kuralı söyler; gizli değerler asla yazılmaz. Geliştirme/test ortamı (Development, test fabrikaları) ve operatör komutları (<c>platform …</c>) bu denetimden muaftır.
/// Saf fonksiyondur (birim testli): <see cref="Validate"/>.
/// </summary>
public static class ProductionStartupValidator
{
    private static readonly string[] ProtectedDatabases = ["sanalprova_dev", "sanalprova_test", "sanalprova_e2e", "master", "tempdb", "model", "msdb"];
    private static readonly string[] PlaceholderFragments = ["changeme", "change-me", "placeholder", "example", "password", "secret", "todo", "xxxx", "0000", "deneme"];

    public static void EnsureValid(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsProduction())
        {
            return;
        }

        var problems = Validate(configuration);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Üretim yapılandırması güvensiz veya eksik; uygulama başlatılmadı:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", problems));
        }
    }

    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var problems = new List<string>();

        // --- Veritabanı: TLS + sertifika doğrulaması, yetkisiz hesap yok, korumalı veritabanı yok
        var connection = configuration.GetConnectionString("Main");
        if (string.IsNullOrWhiteSpace(connection))
        {
            problems.Add("ConnectionStrings:Main tanımlı değil.");
        }
        else
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connection);
                if (builder.Encrypt == SqlConnectionEncryptOption.Optional)
                {
                    problems.Add("ConnectionStrings:Main: Encrypt=True (veya Strict) olmalı (SQL bağlantısı şifreli olmalı).");
                }

                if (builder.TrustServerCertificate)
                {
                    problems.Add("ConnectionStrings:Main: TrustServerCertificate=True kabul edilmez; sunucu sertifikası doğrulanmalı (özel CA için SSL_CERT_DIR/HostNameInCertificate).");
                }

                if (string.Equals(builder.UserID, "sa", StringComparison.OrdinalIgnoreCase) || builder.IntegratedSecurity)
                {
                    problems.Add("ConnectionStrings:Main: 'sa' veya Windows kimliği yerine en az yetkili uygulama hesabı (sanalprova_app) kullanılmalı.");
                }

                if (ProtectedDatabases.Contains(builder.InitialCatalog, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
                {
                    problems.Add("ConnectionStrings:Main: veritabanı adı geliştirme/test/sistem veritabanı olamaz (üretim veritabanı adı verin).");
                }

                if (string.IsNullOrEmpty(builder.Password))
                {
                    problems.Add("ConnectionStrings:Main: parola boş.");
                }
            }
            catch (ArgumentException)
            {
                problems.Add("ConnectionStrings:Main geçerli bir bağlantı dizesi değil.");
            }
        }

        // --- Gizli anahtarlar
        Secret(configuration, "Storage:SigningKey", problems);
        Secret(configuration, "Security:SetupTokenKey", problems);
        var signing = configuration["Storage:SigningKey"];
        if (!string.IsNullOrEmpty(signing) && signing == configuration["Security:SetupTokenKey"])
        {
            problems.Add("Storage:SigningKey ve Security:SetupTokenKey farklı değerler olmalı.");
        }

        // --- Depolama ve anahtar halkası kalıcı olmalı
        if (!string.Equals(configuration["Storage:Provider"] ?? "Local", "Local", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Storage:Provider şimdilik yalnızca 'Local' desteklenir.");
        }

        var root = configuration["Storage:LocalRoot"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
        {
            problems.Add("Storage:LocalRoot mutlak yol olmalı (kalıcı birim).");
        }

        var keys = configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keys) || !Path.IsPathRooted(keys))
        {
            problems.Add("DataProtection:KeysPath mutlak yol olmalı (çerez/antiforgery anahtarları kalıcı birimde saklanmalı; yoksa her yeniden başlatmada oturumlar düşer).");
        }

        var journal = configuration["Retention:DeletionJournalPath"];
        if (string.IsNullOrWhiteSpace(journal) || !Path.IsPathRooted(journal))
        {
            problems.Add("Retention:DeletionJournalPath mutlak yol olmalı (silme günlüğü; yedekten geri yüklemede silinmiş kişisel verinin yeniden silinmesi için gerekli).");
        }
        else if (!string.IsNullOrWhiteSpace(root) && IsUnder(journal, root))
        {
            problems.Add("Retention:DeletionJournalPath, Storage:LocalRoot dışında olmalı (depo yedeğiyle birlikte geri yüklenip eskimesin).");
        }

        // --- Ağ: ana makine adı ve güvenilen vekil
        var hosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Trim() == "*")
        {
            problems.Add("AllowedHosts '*' olamaz; yalnızca site adresi(leri) verilmeli.");
        }

        problems.AddRange(TrustedForwardedHeaders.Validate(configuration));
        if (!TrustedForwardedHeaders.IsConfigured(configuration))
        {
            problems.Add("ForwardedHeaders:KnownProxies veya KnownNetworks tanımlı değil (ters vekil arkasında istemci IP'si/şema için gerekli; vekilsiz çalışma desteklenmez).");
        }

        // --- Demo/geliştirme özellikleri kapalı
        if (configuration.GetValue("Seed:Enabled", false))
        {
            problems.Add("Seed:Enabled üretimde false olmalı.");
        }

        // --- Gerçek (ücretli) AI: anahtar ve güvenli kapılar
        var live = !configuration.GetValue("Generation:UseMockProviders", true);
        var allowPaid = configuration.GetValue("Generation:Budget:AllowPaidCalls", false);
        if (live && !allowPaid)
        {
            problems.Add("Generation:UseMockProviders=false iken Generation:Budget:AllowPaidCalls=true olmalı (aksi halde tüm üretimler reddedilir); mock'ta kalmak için UseMockProviders=true bırakın.");
        }

        if (allowPaid && !live)
        {
            problems.Add("Generation:Budget:AllowPaidCalls=true yalnızca UseMockProviders=false ile anlamlıdır.");
        }

        if (allowPaid)
        {
            if (!configuration.GetValue("Generation:Budget:Enforce", true))
            {
                problems.Add("Ücretli çağrılar açıkken Generation:Budget:Enforce=false olamaz.");
            }

            var daily = configuration.GetValue("Generation:Budget:DailyLimitUsd", 10m);
            var monthly = configuration.GetValue("Generation:Budget:MonthlyLimitUsd", 150m);
            var perJob = configuration.GetValue("Generation:Budget:MaxJobCostUsd", 0.5m);
            if (daily <= 0 || monthly <= 0 || perJob <= 0 || perJob > daily || daily > monthly)
            {
                problems.Add("Generation:Budget sınırları tutarsız (0 < MaxJobCostUsd ≤ DailyLimitUsd ≤ MonthlyLimitUsd olmalı).");
            }

            foreach (var jobType in new[] { "OutfitSet", "FreeCombination" })
            {
                var provider = configuration[$"Generation:Strategies:{jobType}:Provider"];
                if (jobType == "FreeCombination" && !configuration.GetValue("Generation:LiveFreeCombinationEnabled", false))
                {
                    continue;
                }

                var keyName = provider switch
                {
                    "FalSeedream" or "FalGptImage" => "Ai:Fal:ApiKey",
                    "Pruna" => "Ai:Pruna:ApiKey",
                    "Fashn" => "Ai:Fashn:ApiKey",
                    "Gemini" => "Ai:Gemini:ApiKey",
                    _ => null,
                };
                if (keyName is not null && string.IsNullOrWhiteSpace(configuration[keyName]))
                {
                    problems.Add($"{keyName} tanımlı değil ({jobType} stratejisi '{provider}' sağlayıcısını kullanıyor).");
                }
            }
        }

        return problems;
    }

    private static void Secret(IConfiguration configuration, string key, List<string> problems)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
        {
            problems.Add($"{key} tanımlı değil veya 32 karakterden kısa.");
            return;
        }

        if (PlaceholderFragments.Any(f => value.Contains(f, StringComparison.OrdinalIgnoreCase)) || value.Distinct().Count() < 8)
        {
            problems.Add($"{key} yer tutucu/öngörülebilir bir değere benziyor; rastgele üretilmiş bir değer kullanın.");
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
