using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Platform;

/// <summary>
/// Platform operatörü komutları (M5.1g): <c>dotnet SanalProva.Api.dll platform &lt;komut&gt; ...</c>. <b>HTTP üzerinden erişilemez</b>: komut satırı argümanlarıyla çalışır, web sunucusu
/// (Kestrel) hiç başlatılmaz ve işlem bitince süreç sonlanır; hiçbir uç nokta/hub bu sınıfı çağırmaz (mimari testlerle sabit). Yetki, sunucudaki veritabanı bağlantı bilgisine
/// sahip olmaktır (<c>ConnectionStrings__Main</c> ortam değişkeni/user-secrets); bu bilgi API üzerinden elde edilemez.
/// <para>
/// Her değiştirici komut <c>--confirm</c> ister; olmadan <b>hiçbir veritabanı yazması yapılmaz</b> (yalnızca okuma ve plan çıktısı). Kurulum bağlantıları yalnızca standart çıktıya
/// <b>bir kez</b> yazılır; günlüğe, dosyaya, denetim kaydına veya veritabanına düz olarak girmez (veritabanında yalnızca HMAC özeti). Denetim kayıtları kişisel veri içermez.
/// </para>
/// Komutlar: <c>bootstrap</c>, <c>reissue-link</c>, <c>revoke</c>, <c>reset-access</c>, <c>paid-ai</c>. Ayrıntı: <c>docs/14-platform-operator-komutlari.md</c>.
/// </summary>
public static class PlatformCommands
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitRefused = 3;
    public const int ExitNotFound = 4;

    public const string PlatformSlug = "platform";
    public const string PlatformName = "Sanal Prova Platform";

    public static bool IsCommand(string[] args) => args.Length > 0 && string.Equals(args[0], "platform", StringComparison.Ordinal);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (args.Length < 2)
        {
            await output.WriteLineAsync(Usage);
            return ExitUsage;
        }

        var options = CommandOptions.Parse(args.Skip(2).ToArray());
        if (options.Error is not null)
        {
            await output.WriteLineAsync($"HATA: {options.Error}");
            await output.WriteLineAsync(Usage);
            return ExitUsage;
        }

        await using var scope = services.CreateAsyncScope();
        var context = new Context(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<AccountSetupTokenService>(), scope.ServiceProvider.GetRequiredService<AuditTrail>(),
            scope.ServiceProvider.GetRequiredService<IOptions<SetupTokenOptions>>().Value, options, output);

        // Anahtar yoksa/zayıfsa token üretilmez (web sunucusundaki ValidateOnStart bu yolda çalışmaz)
        if (!Validator.TryValidateObject(context.TokenOptions, new ValidationContext(context.TokenOptions), null, validateAllProperties: true))
        {
            await output.WriteLineAsync("HATA: Security:SetupTokenKey yapılandırılmamış veya 32 karakterden kısa (ortam değişkeni Security__SetupTokenKey veya user-secrets).");
            return ExitFailed;
        }

        try
        {
            return args[1] switch
            {
                "bootstrap" => await BootstrapAsync(context, ct),
                "reissue-link" => await ReissueAsync(context, ct),
                "revoke" => await RevokeAsync(context, ct),
                "reset-access" => await ResetAccessAsync(context, ct),
                "paid-ai" => await PaidAiAsync(context, ct),
                _ => await UnknownAsync(output, args[1]),
            };
        }
        catch (DbUpdateException ex)
        {
            // Ayrıntı (parametre değerleri dahil) yazılmaz: yalnızca tür; işlem geri alındı
            await output.WriteLineAsync($"HATA: veritabanı yazması başarısız ({ex.GetType().Name}); hiçbir değişiklik yapılmadı.");
            return ExitFailed;
        }
    }

    private static async Task<int> UnknownAsync(TextWriter output, string command)
    {
        await output.WriteLineAsync($"HATA: bilinmeyen komut '{command}'.");
        await output.WriteLineAsync(Usage);
        return ExitUsage;
    }

    private const string Usage = """
        Kullanım: dotnet SanalProva.Api.dll platform <komut> [seçenekler]
          bootstrap     --email <e-posta> --name <ad> [--additional] [--base-url <url>] [--confirm]   İlk (veya ek) platform yöneticisi + 72 saatlik şifre bağlantısı
          reissue-link  --email <e-posta> [--base-url <url>] [--confirm]                              Şifresini henüz belirlememiş yöneticinin bağlantısını yeniden üretir (öncekiler iptal)
          revoke        --email <e-posta> [--confirm]                                                 Yöneticinin açık kurulum bağlantılarını iptal eder
          reset-access  --email <e-posta> [--base-url <url>] [--confirm]                              Erişim kurtarma: parolayı siler, güvenlik damgasını yeniler, yeni bağlantı üretir
          paid-ai       --org <slug> (--enable | --disable) [--confirm]                               Firmanın ücretli AI yetkisini değiştirir (denetime yazılır)
        --confirm olmadan hiçbir veritabanı değişikliği yapılmaz (kuru çalışma).
        """;

    // ------------------------------------------------------------------ bootstrap

    private static async Task<int> BootstrapAsync(Context c, CancellationToken ct)
    {
        var email = c.Options.Email;
        var name = c.Options.Name;
        if (email is null || name is null)
        {
            return await UsageErrorAsync(c, "--email ve --name zorunludur.");
        }

        var activeAdmins = await c.Db.Users.AsNoTracking().CountAsync(u => u.IsPlatformAdmin && u.IsActive, ct);
        if (activeAdmins > 0 && !c.Options.Additional)
        {
            await c.Out.WriteLineAsync($"REDDEDİLDİ: {activeAdmins} aktif platform yöneticisi zaten var. İkinci yönetici yalnızca --additional ile oluşturulur (bilinçli karar).");
            return ExitRefused;
        }

        if (await c.Users.FindByEmailAsync(email) is not null)
        {
            await c.Out.WriteLineAsync("REDDEDİLDİ: bu e-posta adresiyle zaten bir hesap var (e-posta sistem genelinde benzersizdir).");
            return ExitRefused;
        }

        var platformOrganization = await c.Db.Organizations.SingleOrDefaultAsync(o => o.Slug == PlatformSlug, ct);
        await c.Out.WriteLineAsync($"PLAN: platform yöneticisi oluşturulacak: {email} ({name}); firma: {(platformOrganization is null ? $"'{PlatformName}' oluşturulacak" : $"mevcut '{PlatformSlug}'")}; 72 saatlik tek kullanımlık şifre bağlantısı.");
        if (!c.Options.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir değişiklik yapılmadı.");
            return ExitOk;
        }

        await using var transaction = await c.Db.Database.BeginTransactionAsync(ct);
        if (platformOrganization is null)
        {
            platformOrganization = new Organization { Name = PlatformName, Slug = PlatformSlug };
            c.Db.Organizations.Add(platformOrganization);
            await c.Db.SaveChangesAsync(ct);
        }

        // Parolasız hesap: giriş yapılamaz, ta ki bağlantıyla şifre belirlenene kadar. Platform yöneticisinin hiçbir firmada mağaza rolü yoktur.
        var admin = new AppUser { OrganizationId = platformOrganization.Id, DisplayName = name, Email = email, UserName = email, IsPlatformAdmin = true };
        var created = await c.Users.CreateAsync(admin);
        if (!created.Succeeded)
        {
            await c.Out.WriteLineAsync("HATA: hesap oluşturulamadı: " + string.Join(", ", created.Errors.Select(e => e.Code)));
            return ExitFailed;
        }

        var (token, entity) = await c.Tokens.IssueAsync(admin.Id, platformOrganization.Id, AccountSetupTokenPurpose.InitialPassword, LinkTtl(c), null, ct);
        c.Audit.RecordFor(platformOrganization.Id, null, "Platform.AdminBootstrapped", nameof(AppUser), admin.Id);
        c.Audit.RecordFor(platformOrganization.Id, null, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id);
        await c.Db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await PrintLinkAsync(c, token, entity.ExpiresAt);
        return ExitOk;
    }

    // ------------------------------------------------------------------ reissue-link / revoke / reset-access

    private static async Task<AppUser?> FindAdminAsync(Context c)
    {
        var email = c.Options.Email;
        if (email is null)
        {
            await UsageErrorAsync(c, "--email zorunludur.");
            return null;
        }

        var admin = await c.Users.FindByEmailAsync(email);
        if (admin is null || !admin.IsPlatformAdmin)
        {
            await c.Out.WriteLineAsync("BULUNAMADI: bu e-posta ile bir platform yöneticisi yok.");
            return null;
        }

        return admin;
    }

    private static async Task<int> ReissueAsync(Context c, CancellationToken ct)
    {
        if (await FindAdminAsync(c) is not { } admin)
        {
            return c.Options.Email is null ? ExitUsage : ExitNotFound;
        }

        if (admin.PasswordHash is not null)
        {
            await c.Out.WriteLineAsync("REDDEDİLDİ: yönetici şifresini zaten belirlemiş. Erişim kaybı için 'reset-access' kullanın.");
            return ExitRefused;
        }

        await c.Out.WriteLineAsync($"PLAN: {admin.Email} için önceki açık bağlantılar iptal edilecek ve yeni 72 saatlik bağlantı üretilecek.");
        if (!c.Options.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir değişiklik yapılmadı.");
            return ExitOk;
        }

        return await IssueAndAuditAsync(c, admin, ct);
    }

    private static async Task<int> IssueAndAuditAsync(Context c, AppUser admin, CancellationToken ct)
    {
        await using var transaction = await c.Db.Database.BeginTransactionAsync(ct);
        var (token, entity) = await c.Tokens.IssueAsync(admin.Id, admin.OrganizationId, AccountSetupTokenPurpose.InitialPassword, LinkTtl(c), null, ct);
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id);
        await c.Db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PrintLinkAsync(c, token, entity.ExpiresAt);
        return ExitOk;
    }

    private static async Task<int> RevokeAsync(Context c, CancellationToken ct)
    {
        if (await FindAdminAsync(c) is not { } admin)
        {
            return c.Options.Email is null ? ExitUsage : ExitNotFound;
        }

        await c.Out.WriteLineAsync($"PLAN: {admin.Email} için açık kurulum bağlantıları iptal edilecek.");
        if (!c.Options.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir değişiklik yapılmadı.");
            return ExitOk;
        }

        await using var transaction = await c.Db.Database.BeginTransactionAsync(ct);
        var revoked = await c.Tokens.RevokeOpenAsync(admin.Id, AccountSetupTokenPurpose.InitialPassword, ct);
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.SetupLinkRevoked", nameof(AppUser), admin.Id);
        await c.Db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await c.Out.WriteLineAsync($"TAMAM: {revoked} açık bağlantı iptal edildi.");
        return ExitOk;
    }

    /// <summary>
    /// Erişim kurtarma (parolayı unutan/kaybeden yönetici): parola silinir, güvenlik damgası yenilenir (kayıtlı oturum çerezleri sonraki damga denetiminde — varsayılan en geç
    /// 30 dk — düşer) ve yeni tek kullanımlık bağlantı üretilir. Yetki yine yalnızca sunucuya/veritabanına erişimdir. Yeni bir HTTP yüzeyi açmaz: mevcut şifre belirleme uç noktasını kullanır.
    /// </summary>
    private static async Task<int> ResetAccessAsync(Context c, CancellationToken ct)
    {
        if (await FindAdminAsync(c) is not { } admin)
        {
            return c.Options.Email is null ? ExitUsage : ExitNotFound;
        }

        await c.Out.WriteLineAsync($"PLAN: {admin.Email} için parola silinecek, güvenlik damgası yenilenecek (oturumlar düşer), yeni 72 saatlik bağlantı üretilecek.");
        if (!c.Options.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir değişiklik yapılmadı.");
            return ExitOk;
        }

        await using var transaction = await c.Db.Database.BeginTransactionAsync(ct);
        if (admin.PasswordHash is not null)
        {
            var removed = await c.Users.RemovePasswordAsync(admin);
            if (!removed.Succeeded)
            {
                await c.Out.WriteLineAsync("HATA: parola silinemedi; hiçbir değişiklik yapılmadı.");
                return ExitFailed;
            }
        }

        await c.Users.UpdateSecurityStampAsync(admin);
        var (token, entity) = await c.Tokens.IssueAsync(admin.Id, admin.OrganizationId, AccountSetupTokenPurpose.InitialPassword, LinkTtl(c), null, ct);
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.AccessReset", nameof(AppUser), admin.Id);
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id);
        await c.Db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PrintLinkAsync(c, token, entity.ExpiresAt);
        return ExitOk;
    }

    // ------------------------------------------------------------------ paid-ai

    private static async Task<int> PaidAiAsync(Context c, CancellationToken ct)
    {
        if (c.Options.Org is null || c.Options.Enable == c.Options.Disable)
        {
            return await UsageErrorAsync(c, "--org <slug> ve (--enable veya --disable) zorunludur (ikisi birden olamaz).");
        }

        var organization = await c.Db.Organizations.SingleOrDefaultAsync(o => o.Slug == c.Options.Org, ct);
        if (organization is null)
        {
            await c.Out.WriteLineAsync("BULUNAMADI: bu slug ile firma yok.");
            return ExitNotFound;
        }

        var target = c.Options.Enable;
        var current = organization.Settings.PaidAiEnabled;
        await c.Out.WriteLineAsync($"PLAN: '{organization.Slug}' ücretli AI: {(current ? "AÇIK" : "KAPALI")} → {(target ? "AÇIK" : "KAPALI")}. Global kapılar (gerçek mod, AllowPaidCalls, API anahtarı, bütçe) bundan bağımsız aynen geçerlidir.");
        if (current == target)
        {
            await c.Out.WriteLineAsync("DEĞİŞİKLİK YOK: firma zaten bu durumda.");
            return ExitOk;
        }

        if (!c.Options.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir değişiklik yapılmadı.");
            return ExitOk;
        }

        // Bu sınıf, PaidAiEnabled'ı yazan TEK yerdir (kaynak taraması testi: PaidAiGateTests)
        organization.Settings.PaidAiEnabled = target;
        c.Audit.RecordFor(organization.Id, null, target ? "Platform.PaidAiEnabled" : "Platform.PaidAiDisabled", nameof(Organization), organization.Id);
        await c.Db.SaveChangesAsync(ct);
        await c.Out.WriteLineAsync($"TAMAM: '{organization.Slug}' ücretli AI {(target ? "AÇILDI" : "KAPATILDI")} (denetim kaydı yazıldı).");
        return ExitOk;
    }

    // ------------------------------------------------------------------ yardımcılar

    private static TimeSpan LinkTtl(Context c) => TimeSpan.FromHours(c.TokenOptions.InitialPasswordTtlHours);

    private static async Task<int> UsageErrorAsync(Context c, string message)
    {
        await c.Out.WriteLineAsync($"HATA: {message}");
        await c.Out.WriteLineAsync(Usage);
        return ExitUsage;
    }

    /// <summary>Bağlantı yalnızca standart çıktıya yazılır (günlüğe/dosyaya değil) ve bir daha gösterilemez.</summary>
    private static async Task PrintLinkAsync(Context c, string token, DateTimeOffset expiresAt)
    {
        var link = (c.Options.BaseUrl?.TrimEnd('/') ?? string.Empty) + PlatformOnboardingService.BuildLink(token);
        await c.Out.WriteLineAsync("TAMAM. Tek kullanımlık şifre belirleme bağlantısı (bir daha gösterilmez, kaydetmeyin; süresi dolunca yeniden üretin):");
        await c.Out.WriteLineAsync(link);
        await c.Out.WriteLineAsync($"Geçerlilik sonu (UTC): {expiresAt:u}");
    }

    private sealed record Context(
        AppDbContext Db, UserManager<AppUser> Users, AccountSetupTokenService Tokens, AuditTrail Audit, SetupTokenOptions TokenOptions, CommandOptions Options, TextWriter Out);

    private sealed record CommandOptions(string? Error, string? Email, string? Name, string? Org, string? BaseUrl, bool Confirm, bool Additional, bool Enable, bool Disable)
    {
        public static CommandOptions Parse(string[] args)
        {
            string? email = null, name = null, org = null, baseUrl = null;
            bool confirm = false, additional = false, enable = false, disable = false;
            for (var i = 0; i < args.Length; i++)
            {
                string Value(string flag) => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : throw new FormatException($"{flag} bir değer ister.");
                try
                {
                    switch (args[i])
                    {
                        case "--email": email = Value("--email").Trim(); break;
                        case "--name": name = Value("--name").Trim(); break;
                        case "--org": org = Value("--org").Trim(); break;
                        case "--base-url": baseUrl = Value("--base-url").Trim(); break;
                        case "--confirm": confirm = true; break;
                        case "--additional": additional = true; break;
                        case "--enable": enable = true; break;
                        case "--disable": disable = true; break;
                        default: return Fail($"bilinmeyen seçenek '{args[i]}'.");
                    }
                }
                catch (FormatException ex)
                {
                    return Fail(ex.Message);
                }
            }

            if (email is not null && !IsEmail(email))
            {
                return Fail("--email geçerli bir e-posta adresi olmalı.");
            }

            if (name is not null && name.Length is < 2 or > 80)
            {
                return Fail("--name 2–80 karakter olmalı.");
            }

            if (baseUrl is not null && !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            {
                return Fail("--base-url mutlak http(s) adresi olmalı.");
            }

            return new CommandOptions(null, email, name, org, baseUrl, confirm, additional, enable, disable);

            static CommandOptions Fail(string message) => new(message, null, null, null, null, false, false, false, false);
        }

        private static bool IsEmail(string value) =>
            value.Length <= 254 && MailAddress.TryCreate(value, out var address) && string.Equals(address.Address, value, StringComparison.Ordinal);
    }
}
