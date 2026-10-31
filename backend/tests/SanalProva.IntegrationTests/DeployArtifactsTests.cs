using System.Text.RegularExpressions;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Pilot Faz A: üretim dağıtım dosyalarının (Caddy, Compose, Dockerfile, betikler) güvenlik ve yönlendirme sözleşmesi. Docker/Caddy çalıştırılmadan, dosya içeriği düzeyinde
/// bozulmayı yakalar (ör. biri SQL portunu yayımlarsa, TLS doğrulamasını kapatırsa, sır eklerse, /tv yönlendirmesini silerse test kırılır).
/// GERÇEK Caddy/Docker doğrulaması bu makinede yapılamadı (docs/19 "Doğrulama durumu").
/// </summary>
public sealed class DeployArtifactsTests
{
    private static string Read(string relative) => File.ReadAllText(MigrationLab.RepoFile(relative));

    private static string Compose => Read("deploy/docker-compose.prod.yml");

    private static string Service(string name)
    {
        // YAML'ı bağımlılıksız ayrıştırmak yerine: "  <ad>:" satırından bir sonraki iki girintili servis başlığına kadar
        var match = Regex.Match(Compose, $@"^  {Regex.Escape(name)}:\s*$(?<body>.*?)(?=^  [a-z][a-z0-9-]*:\s*$|^[a-z]+:\s*$|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"servis bulunamadı: {name}");
        return match.Groups["body"].Value;
    }

    // ---------------------------------------------------------------- Caddy

    [Fact]
    public void Caddy_icerik_guvenlik_politikasi_siki_tanimlidir_ve_satir_ici_betik_gerektirmez()
    {
        var site = Read("frontend/caddy/common.caddy");
        var match = Regex.Match(site, @"Content-Security-Policy\s+""([^""]+)""");
        Assert.True(match.Success, "common.caddy içinde Content-Security-Policy yok.");
        var policy = match.Groups[1].Value;
        foreach (var directive in new[] { "default-src 'self'", "script-src 'self'", "object-src 'none'", "frame-ancestors 'none'", "base-uri 'self'", "form-action 'self'", "worker-src 'self'", "connect-src 'self' wss://{http.request.host}" })
        {
            Assert.Contains(directive, policy, StringComparison.Ordinal);
        }

        // Betik kaynağı gevşetilemez: unsafe-inline/unsafe-eval/joker/harici kaynak yok (stil için unsafe-inline Mantine nedeniyle bilinçli istisnadır)
        var script = Regex.Match(policy, "script-src([^;]*)").Groups[1].Value;
        Assert.DoesNotContain("unsafe", script, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", policy, StringComparison.Ordinal);

        // Derlenmemiş TV girişinde satır içi betik yok (CSP script-src 'self'); yedek betikler harici dosyadır ve gerçekten vardır
        var tv = Read("frontend/tv.html");
        Assert.DoesNotMatch(@"<script(?![^>]*src=)[^>]*>", tv);
        Assert.True(File.Exists(MigrationLab.RepoFile("frontend/public/tv-nomodule.js")));
        Assert.True(File.Exists(MigrationLab.RepoFile("frontend/public/tv-watchdog.js")));
        Assert.DoesNotMatch(@"<script(?![^>]*src=)[^>]*>", Read("frontend/index.html"));
    }

    [Fact]
    public void Caddy_yonlendirmeleri_api_hubs_files_tv_ve_spa_icin_tanimlidir()
    {
        var site = Read("frontend/caddy/common.caddy");

        Assert.Contains("@backend path /api/* /hubs/* /files/*", site, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy api:8080", site, StringComparison.Ordinal);
        Assert.Contains("flush_interval -1", site, StringComparison.Ordinal); // SignalR/akış tamponlanmaz
        Assert.Contains("@tv path /tv /tv/", site, StringComparison.Ordinal);
        Assert.Contains("rewrite * /tv.html", site, StringComparison.Ordinal);
        Assert.Contains("try_files {path} /index.html", site, StringComparison.Ordinal); // SPA yönlendirmesi
        Assert.Contains("/assets/*", site, StringComparison.Ordinal);
        Assert.Contains("immutable", site, StringComparison.Ordinal);
        Assert.Contains("request_body", site, StringComparison.Ordinal); // yükleme boyutu sınırı
        // Sıra: sağlık/backend/tv yakalayıcıdan ÖNCE gelmeli (handle blokları sırayla değerlendirilir)
        Assert.True(site.IndexOf("handle @backend", StringComparison.Ordinal) < site.LastIndexOf("handle {", StringComparison.Ordinal));
        Assert.True(site.IndexOf("handle @tv", StringComparison.Ordinal) < site.LastIndexOf("handle {", StringComparison.Ordinal));
    }

    [Fact]
    public void Caddy_guvenlik_basliklari_kamera_izni_saglik_kapali_ve_erisim_gunlugu_yok()
    {
        var site = Read("frontend/caddy/common.caddy");

        Assert.Contains("Strict-Transport-Security", site, StringComparison.Ordinal);
        Assert.Contains("X-Content-Type-Options \"nosniff\"", site, StringComparison.Ordinal);
        Assert.Contains("X-Frame-Options \"DENY\"", site, StringComparison.Ordinal);
        Assert.Contains("camera=(self)", site, StringComparison.Ordinal); // müşteri fotoğrafı için kamera yalnızca kendi kaynağında
        Assert.Contains("microphone=()", site, StringComparison.Ordinal);
        Assert.Matches(@"@health path /health /health/\*\s+handle @health \{\s+respond 404", site); // sağlık uç noktaları dışarıya kapalı
        // Erişim günlüğü yok: imzalı görsel adresleri sorgu dizesinde imza taşır (yorum satırları hariç gerçek "log" direktifi aranır)
        Assert.DoesNotMatch(@"(?m)^\s*log\b", site);
        Assert.DoesNotContain("tls internal", site, StringComparison.Ordinal); // iç CA yalnızca Caddyfile.internal'da
    }

    [Fact]
    public void Caddy_giris_dosyalari_ortak_siteyi_icerir_internal_yalnizca_internal_dosyada_tls_internal_kullanir()
    {
        var acme = Read("frontend/caddy/Caddyfile");
        var internalCa = Read("frontend/caddy/Caddyfile.internal");

        Assert.Contains("import /etc/caddy/common.caddy", acme, StringComparison.Ordinal);
        Assert.Contains("{$SITE_ADDRESS}", acme, StringComparison.Ordinal);
        Assert.DoesNotContain("tls internal", acme, StringComparison.Ordinal);
        Assert.Contains("tls internal", internalCa, StringComparison.Ordinal);
        Assert.Contains("import sp_site", acme, StringComparison.Ordinal);
        Assert.Contains("import sp_site", internalCa, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_imaji_cok_asamalidir_sir_icermez_iki_giris_noktasini_dogrular()
    {
        var dockerfile = Read("frontend/Dockerfile");

        Assert.Contains("FROM node:22-alpine AS build", dockerfile, StringComparison.Ordinal);
        Assert.Contains("npm ci", dockerfile, StringComparison.Ordinal);
        Assert.Contains("FROM caddy:2-alpine AS web", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY --from=build /src/dist /srv", dockerfile, StringComparison.Ordinal);
        Assert.Contains("/srv/tv.html", dockerfile, StringComparison.Ordinal); // derleme zamanı: TV girişi var mı
        Assert.DoesNotMatch(@"(?mi)^\s*(ENV|ARG)\s+\w*(PASSWORD|SECRET|KEY|TOKEN)", dockerfile);
        Assert.Contains("node_modules/", Read("frontend/.dockerignore"), StringComparison.Ordinal);
        Assert.Contains(".env", Read("frontend/.dockerignore"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Compose

    [Fact]
    public void Compose_yalnizca_web_80_443_yayimlar_sqlserver_ve_api_portu_yayimlanmaz()
    {
        Assert.DoesNotContain("ports:", Service("sqlserver"), StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", Service("api"), StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", Service("ops"), StringComparison.Ordinal);
        var web = Service("web");
        Assert.Contains("\"80:80\"", web, StringComparison.Ordinal);
        Assert.Contains("\"443:443\"", web, StringComparison.Ordinal);
        // Compose'ta yayımlanan tüm portlar
        var published = Regex.Matches(Compose, @"^\s+- ""?(\d+):(\d+)(/udp)?""?\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(["443", "80"], published);
    }

    [Fact]
    public void Compose_agi_icten_yalitilmis_sabit_alt_ag_ve_guvenilen_vekil_yapilandirmasi_vardir()
    {
        Assert.Matches(@"backend:\s+internal: true", Compose);
        Assert.Contains("ipam:", Compose, StringComparison.Ordinal);
        Assert.Contains("${PROXY_SUBNET:-172.31.250.0/24}", Compose, StringComparison.Ordinal);
        var api = Service("api");
        Assert.Contains("ForwardedHeaders__KnownNetworks: ${PROXY_SUBNET", api, StringComparison.Ordinal);
        Assert.Contains("AllowedHosts: ${SITE_ADDRESS:?}", api, StringComparison.Ordinal);
        Assert.Contains("networks: [backend, edge]", api, StringComparison.Ordinal);
        Assert.Contains("networks: [backend]", Service("sqlserver"), StringComparison.Ordinal); // SQL Server dış dünyaya çıkamaz
        Assert.Contains("networks: [edge]", Service("web"), StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_uretim_ortami_TLS_dogrulamali_ve_guvenli_varsayilanlarla_calisir()
    {
        var api = Service("api");
        Assert.Contains("ASPNETCORE_ENVIRONMENT: Production", api, StringComparison.Ordinal);
        Assert.Contains("Encrypt=True;TrustServerCertificate=False;HostNameInCertificate=sqlserver", api, StringComparison.Ordinal);
        Assert.DoesNotContain("TrustServerCertificate=True", Compose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("User ID=sanalprova_app", api, StringComparison.Ordinal);
        Assert.DoesNotContain("User ID=sa;", Compose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Seed__Enabled: \"false\"", api, StringComparison.Ordinal);
        Assert.Contains("Generation__UseMockProviders: ${USE_MOCK_PROVIDERS:-true}", api, StringComparison.Ordinal); // varsayılan: mock
        Assert.Contains("Generation__Budget__AllowPaidCalls: ${ALLOW_PAID_CALLS:-false}", api, StringComparison.Ordinal); // varsayılan: ücretli kapalı
        Assert.Contains("Retention__DeletionJournalPath: /data/journal/deleted-sessions.log", api, StringComparison.Ordinal);
        Assert.Contains("DataProtection__KeysPath: /data/dpkeys", api, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges:true", api, StringComparison.Ordinal);
        Assert.Contains("cap_drop: [ALL]", api, StringComparison.Ordinal);
        Assert.Contains("forceencryption = 1", Read("deploy/sqlserver/mssql.conf"), StringComparison.Ordinal);
        Assert.Contains("tlsprotocols = 1.2", Read("deploy/sqlserver/mssql.conf"), StringComparison.Ordinal);
        // Silme günlüğü depo biriminden AYRI birimde (depo yedeğiyle eskimesin)
        Assert.Contains("api-journal:/data/journal", api, StringComparison.Ordinal);
        Assert.Contains("api-storage:/data/storage", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_sirlari_ortamdan_zorunlu_alir_hicbir_sir_degeri_yazili_degildir()
    {
        // Her gizli değişken ":?" (zorunlu, eksikse hata) ile bağlanır; sabit bir parola/anahtar yoktur
        foreach (var variable in new[] { "MSSQL_SA_PASSWORD", "SP_MIGRATOR_PW", "SP_APP_PW", "SP_BACKUP_PW", "STORAGE_SIGNING_KEY", "SETUP_TOKEN_KEY", "SITE_ADDRESS", "DB_NAME" })
        {
            Assert.Matches($@"\$\{{{variable}:\?[^}}]*\}}", Compose);
        }

        Assert.DoesNotMatch(@"(?i)(password|signingkey|setuptokenkey)\s*[:=]\s*[""']?(?!\$\{)[A-Za-z0-9+/=_-]{12,}", Compose);
        Assert.Contains("backup_passphrase", Compose, StringComparison.Ordinal);
        Assert.Contains("file: ./secrets/backup_passphrase", Compose, StringComparison.Ordinal); // Docker sırrı (dosyadan)
        Assert.Contains("profiles: [\"ops\"]", Service("ops"), StringComparison.Ordinal); // yedek/geri yükleme servisi sürekli çalışmaz
    }

    [Fact]
    public void Compose_baslangic_sirasi_saglik_provision_migrate_harden_api_web_seklindedir()
    {
        Assert.Contains("db-provision:", Service("db-migrate"), StringComparison.Ordinal);
        Assert.Contains("db-migrate:", Service("db-harden"), StringComparison.Ordinal);
        Assert.Contains("db-harden:", Service("api"), StringComparison.Ordinal);
        Assert.Contains("service_completed_successfully", Service("api"), StringComparison.Ordinal);
        Assert.Contains("service_healthy", Service("web"), StringComparison.Ordinal);
        Assert.Contains("service_healthy", Service("db-provision"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- depo hijyeni

    [Fact]
    public void Gizli_dosyalar_gitignore_ile_dislanir_dagitim_dosyalari_LF_ile_tutulur()
    {
        var ignore = Read(".gitignore");
        foreach (var rule in new[] { ".env.*", "deploy/secrets/", "deploy/backups/", "deploy/.env.prod", "*.pfx" })
        {
            Assert.Contains(rule, ignore, StringComparison.Ordinal);
        }

        var attributes = Read(".gitattributes");
        Assert.Contains("deploy/** text eol=lf", attributes, StringComparison.Ordinal);
        Assert.Contains("frontend/caddy/** text eol=lf", attributes, StringComparison.Ordinal);
        foreach (var script in new[] { "deploy/ops/ops.sh", "deploy/scripts/prod-env.sh", "deploy/scripts/make-sql-cert.sh", "deploy/scripts/preflight.sh", "deploy/tests/ops-selftest.sh" })
        {
            var text = Read(script);
            Assert.StartsWith("#!/bin/bash", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal); // CRLF Linux'ta betiği bozar (.gitattributes LF'ye çevirir)
        }
    }

    [Fact]
    public void Dagitim_klasorunde_gercek_gorunumlu_sir_yoktur()
    {
        var root = Path.GetDirectoryName(MigrationLab.RepoFile("deploy/docker-compose.prod.yml"))!;
        var secretLike = new Regex(@"Aa1[0-9a-f]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?i)api[_-]?key\s*[:=]\s*[A-Za-z0-9_-]{24,}", RegexOptions.CultureInvariant);
        var offenders = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}secrets{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.EndsWith(".env.prod", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && Path.GetExtension(f) is ".sh" or ".sql" or ".yml" or ".conf" or ".md" or "")
            .Where(f => secretLike.IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(root, f)).ToList();

        Assert.True(offenders.Count == 0, "Sır gibi görünen içerik: " + string.Join(", ", offenders));
    }
}
