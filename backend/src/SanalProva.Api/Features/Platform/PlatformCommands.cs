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
/// Komutlar: <c>bootstrap</c>, <c>reissue-link</c>, <c>revoke</c>, <c>reset-access</c>, <c>paid-ai</c>, <c>budget</c>, <c>credits</c>, <c>reapply-deletions</c>. Ayrıntı: <c>docs/14-platform-operator-komutlari.md</c>.
/// </summary>
public static class PlatformCommands
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitRefused = 3;
    public const int ExitNotFound = 4;

    /// <summary><c>status</c> çıkış kodları: cron/izleyici bunlara göre uyarır.</summary>
    public const int ExitMonitorWarning = 10;
    public const int ExitMonitorCritical = 11;

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
            scope.ServiceProvider.GetRequiredService<IOptions<SetupTokenOptions>>().Value, options, output,
            scope.ServiceProvider.GetRequiredService<IOptions<SanalProva.Infrastructure.Ai.GenerationSettings>>().Value.Budget,
            scope.ServiceProvider.GetRequiredService<SanalProva.Infrastructure.Credits.CreditLedgerService>(),
            scope.ServiceProvider.GetRequiredService<SanalProva.Infrastructure.Retention.DeletionJournal>(), scope.ServiceProvider);

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
                "budget" => await BudgetAsync(context, ct),
                "credits" => await CreditsAsync(context, ct),
                "reapply-deletions" => await ReapplyDeletionsAsync(context, ct),
                "status" => await StatusAsync(context, ct),
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

    /// <summary>
    /// Operatör komutlarının denetim izi: kim (OS kullanıcısı@makine), hangi hedef firma, miktar ve referans. SIR/TOKEN/BAĞLANTI/E-POSTA/NOT yazılmaz
    /// (yalnızca bu allowlist alanları); komut adı denetim kaydının eylem alanındadır.
    /// </summary>
    private static string OperatorDetail(Context c)
    {
        static string Budget(BudgetArg a) => !a.Set ? "-" : a.Value is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
        var o = c.Options;
        var parts = new List<string> { $"cli op={Environment.UserName}@{Environment.MachineName}" };
        if (o.Org is not null) parts.Add($"org={o.Org}");
        if (o.Reference is not null) parts.Add($"ref={o.Reference}");
        if (o.Grant is { } g) parts.Add($"grant={g}");
        if (o.Adjust is { } a) parts.Add($"adjust={a}");
        if (o.Daily.Set || o.Monthly.Set || o.Job.Set) parts.Add($"budget d={Budget(o.Daily)} m={Budget(o.Monthly)} j={Budget(o.Job)}");
        return string.Join(' ', parts);
    }

    private const string Usage = """
        Kullanım: dotnet SanalProva.Api.dll platform <komut> [seçenekler]
          status                                                                                      Pilot izleme (salt okunur): veritabanı, disk, takılı iş, belirsiz maliyet, bütçe, kredi. Çıkış 0=tamam, 10=uyarı, 11=kritik
          bootstrap     --email <e-posta> --name <ad> [--additional] [--base-url <url>] [--confirm]   İlk (veya ek) platform yöneticisi + 72 saatlik şifre bağlantısı
          reissue-link  --email <e-posta> [--base-url <url>] [--confirm]                              Şifresini henüz belirlememiş yöneticinin bağlantısını yeniden üretir (öncekiler iptal)
          revoke        --email <e-posta> [--confirm]                                                 Yöneticinin açık kurulum bağlantılarını iptal eder
          reset-access  --email <e-posta> [--base-url <url>] [--confirm]                              Erişim kurtarma: parolayı siler, güvenlik damgasını yeniler, yeni bağlantı üretir
          paid-ai       --org <slug> (--enable | --disable) [--confirm]                               Firmanın ücretli AI yetkisini değiştirir (denetime yazılır)
          credits       --org <slug> [(--grant <n> | --adjust <±n> --note <gerekçe>) --ref <benzersiz-referans>] [--confirm]   Firma kredisi: gösterir / yükler / düzeltir (referans = idempotency)
          reapply-deletions [--journal <dosya>] [--confirm]   Yedekten geri yükleme sonrası: silme günlüğündeki oturumları yeniden siler (KVKK)
          budget        --org <slug> [--daily <usd|none>] [--monthly <usd|none>] [--job <usd|none>] [--confirm]   Firma AI bütçe sınırları (platform tavanını aşamaz; seçeneksiz: yalnızca gösterir)
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
        c.Audit.RecordFor(platformOrganization.Id, null, "Platform.AdminBootstrapped", nameof(AppUser), admin.Id, OperatorDetail(c));
        c.Audit.RecordFor(platformOrganization.Id, null, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id, OperatorDetail(c));
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
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id, OperatorDetail(c));
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
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.SetupLinkRevoked", nameof(AppUser), admin.Id, OperatorDetail(c));
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
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.AccessReset", nameof(AppUser), admin.Id, OperatorDetail(c));
        c.Audit.RecordFor(admin.OrganizationId, null, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id, OperatorDetail(c));
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
        c.Audit.RecordFor(organization.Id, null, target ? "Platform.PaidAiEnabled" : "Platform.PaidAiDisabled", nameof(Organization), organization.Id, OperatorDetail(c));
        await c.Db.SaveChangesAsync(ct);
        await c.Out.WriteLineAsync($"TAMAM: '{organization.Slug}' ücretli AI {(target ? "AÇILDI" : "KAPATILDI")} (denetim kaydı yazıldı).");
        return ExitOk;
    }

    // ------------------------------------------------------------------ budget

    /// <summary>
    /// Firma AI bütçe sınırları (M5.3). Bu sınıf, <c>OrganizationSettings.DailyBudgetUsd/MonthlyBudgetUsd/MaxJobCostUsd</c>'yi yazan TEK yerdir (kaynak taraması testi).
    /// Değer platform tavanını (<c>Generation:Budget</c>) aşamaz; <c>none</c> firma değerini siler (tavan geçerli olur). Hiçbir seçenek verilmezse yalnızca mevcut durum yazdırılır.
    /// </summary>
    private static async Task<int> BudgetAsync(Context c, CancellationToken ct)
    {
        if (c.Options.Org is null)
        {
            return await UsageErrorAsync(c, "--org <slug> zorunludur.");
        }

        var organization = await c.Db.Organizations.SingleOrDefaultAsync(o => o.Slug == c.Options.Org, ct);
        if (organization is null)
        {
            await c.Out.WriteLineAsync("BULUNAMADI: bu slug ile firma yok.");
            return ExitNotFound;
        }

        var platform = c.Budget;
        var settings = organization.Settings;
        string Show(decimal? own, decimal ceiling) => own is null ? $"platform tavanı ({ceiling.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} USD)" : $"{own.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} USD";
        await c.Out.WriteLineAsync($"MEVCUT '{organization.Slug}': günlük {Show(settings.DailyBudgetUsd, platform.DailyLimitUsd)}; aylık {Show(settings.MonthlyBudgetUsd, platform.MonthlyLimitUsd)}; iş başı {Show(settings.MaxJobCostUsd, platform.MaxJobCostUsd)}.");

        var o = c.Options;
        if (!o.Daily.Set && !o.Monthly.Set && !o.Job.Set)
        {
            return ExitOk; // yalnızca okuma
        }

        foreach (var (name, change, ceiling) in new[] { ("--daily", o.Daily, platform.DailyLimitUsd), ("--monthly", o.Monthly, platform.MonthlyLimitUsd), ("--job", o.Job, platform.MaxJobCostUsd) })
        {
            if (change.Set && change.Value is { } value && value > ceiling)
            {
                await c.Out.WriteLineAsync($"REDDEDİLDİ: {name} değeri platform tavanını ({ceiling.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} USD) aşamaz; tavan yapılandırmayla (Generation:Budget) değişir.");
                return ExitRefused;
            }
        }

        var daily = o.Daily.Set ? o.Daily.Value : settings.DailyBudgetUsd;
        var monthly = o.Monthly.Set ? o.Monthly.Value : settings.MonthlyBudgetUsd;
        var job = o.Job.Set ? o.Job.Value : settings.MaxJobCostUsd;
        if (daily == settings.DailyBudgetUsd && monthly == settings.MonthlyBudgetUsd && job == settings.MaxJobCostUsd)
        {
            await c.Out.WriteLineAsync("DEĞİŞİKLİK YOK: değerler zaten bu durumda.");
            return ExitOk;
        }

        await c.Out.WriteLineAsync($"PLAN: günlük {Show(daily, platform.DailyLimitUsd)}; aylık {Show(monthly, platform.MonthlyLimitUsd)}; iş başı {Show(job, platform.MaxJobCostUsd)}. Çalışan işlerin ayrılmış tutarı etkilenmez; yeni sınır sonraki ücretli çağrıdan itibaren geçerlidir.");
        if (!o.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir değişiklik yapılmadı.");
            return ExitOk;
        }

        settings.DailyBudgetUsd = daily;
        settings.MonthlyBudgetUsd = monthly;
        settings.MaxJobCostUsd = job;
        c.Audit.RecordFor(organization.Id, null, "Platform.BudgetChanged", nameof(Organization), organization.Id, OperatorDetail(c));
        await c.Db.SaveChangesAsync(ct);
        await c.Out.WriteLineAsync($"TAMAM: '{organization.Slug}' bütçe sınırları güncellendi (denetim kaydı yazıldı).");
        return ExitOk;
    }

    // ------------------------------------------------------------------ credits

    private static readonly System.Text.RegularExpressions.Regex ReferencePattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Firma kredisi (M5.4): bakiye gösterir; <c>--grant N</c> yükler, <c>--adjust ±N</c> düzeltir. Bu komut (ve <c>CreditLedgerService</c>) kredi hesabını değiştiren TEK yerdir.
    /// <c>--ref</c> zorunlu ve firma içinde benzersiz işlem referansıdır (idempotency): aynı referansla tekrar çalıştırmak ikinci kez yüklemez; farklı değerle reddedilir.
    /// Düzeltme için <c>--note</c> (gerekçe) zorunludur ve bakiye rezerve tutarın altına inemez. <c>--confirm</c> yoksa hiçbir şey yazılmaz. Gerçek ödeme alma yoktur (M5.5).
    /// </summary>
    private static async Task<int> CreditsAsync(Context c, CancellationToken ct)
    {
        if (c.Options.Org is null)
        {
            return await UsageErrorAsync(c, "--org <slug> zorunludur.");
        }

        var organization = await c.Db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Slug == c.Options.Org, ct);
        if (organization is null)
        {
            await c.Out.WriteLineAsync("BULUNAMADI: bu slug ile firma yok.");
            return ExitNotFound;
        }

        var account = await c.Db.CreditAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(a => a.OrganizationId == organization.Id, ct);
        await c.Out.WriteLineAsync($"MEVCUT '{organization.Slug}': bakiye {account?.BalanceCredits ?? 0}; rezerve {account?.ReservedCredits ?? 0}; kullanılabilir {account?.AvailableCredits ?? 0} kredi.");

        var o = c.Options;
        if (o.Grant is null && o.Adjust is null)
        {
            return ExitOk; // yalnızca okuma
        }

        if (o.Grant is not null && o.Adjust is not null)
        {
            return await UsageErrorAsync(c, "--grant ve --adjust birlikte kullanılamaz.");
        }

        if (o.Reference is null || !ReferencePattern.IsMatch(o.Reference))
        {
            return await UsageErrorAsync(c, "--ref zorunlu: 8–64 karakter (harf, rakam, . _ : -), her işlem için benzersiz (örn. fatura/havale numarası).");
        }

        if (o.Grant is < 0)
        {
            return await UsageErrorAsync(c, "--grant pozitif olmalı (geri alma için --adjust kullanın).");
        }

        var adjusting = o.Adjust is not null;
        var amount = o.Grant ?? o.Adjust!.Value;
        if (adjusting && string.IsNullOrWhiteSpace(o.Note))
        {
            return await UsageErrorAsync(c, "--adjust için --note (gerekçe) zorunludur.");
        }

        await c.Out.WriteLineAsync($"PLAN: '{organization.Slug}' için {(adjusting ? "DÜZELTME" : "YÜKLEME")} {amount:+#;-#;0} kredi; referans {o.Reference}.");
        if (!o.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir değişiklik yapılmadı.");
            return ExitOk;
        }

        var result = await c.Credits.ApplyOperatorEntryAsync(
            c.Db, organization.Id, adjusting ? SanalProva.Domain.Credits.CreditEntryType.Adjustment : SanalProva.Domain.Credits.CreditEntryType.Grant, amount, o.Reference, SanalProva.Domain.Credits.CreditSource.Operator, o.Note, ct);
        switch (result.Status)
        {
            case SanalProva.Infrastructure.Credits.CreditOperatorStatus.Replayed:
                await c.Out.WriteLineAsync($"DEĞİŞİKLİK YOK: bu referans zaten uygulanmış (bakiye {result.BalanceAfter}; rezerve {result.ReservedAfter}).");
                return ExitOk;
            case SanalProva.Infrastructure.Credits.CreditOperatorStatus.ReferenceConflict:
                await c.Out.WriteLineAsync("REDDEDİLDİ: bu referans farklı bir işlem (tür/tutar) için kullanılmış; her işlem için yeni referans verin.");
                return ExitRefused;
            case SanalProva.Infrastructure.Credits.CreditOperatorStatus.WouldGoBelowReserved:
                await c.Out.WriteLineAsync($"REDDEDİLDİ: düzeltme bakiyeyi rezerve tutarın ({result.ReservedAfter}) veya sıfırın altına indirir.");
                return ExitRefused;
            case SanalProva.Infrastructure.Credits.CreditOperatorStatus.Invalid:
                return await UsageErrorAsync(c, "tutar geçersiz (yükleme pozitif, düzeltme sıfırdan farklı olmalı).");
        }

        // Denetim kaydı (kişisel veri yok; tutar ve referans defterde de değiştirilemez biçimde durur). Uygulama başarılı olduktan sonra yazılır; yinelenen çalıştırmada yazılmaz.
        c.Audit.RecordFor(organization.Id, null, adjusting ? "Platform.CreditAdjusted" : "Platform.CreditGranted", nameof(Organization), organization.Id, OperatorDetail(c));
        await c.Db.SaveChangesAsync(ct);
        await c.Out.WriteLineAsync($"TAMAM: '{organization.Slug}' {(adjusting ? "düzeltme" : "yükleme")} uygulandı; bakiye {result.BalanceAfter}, rezerve {result.ReservedAfter} (denetim kaydı yazıldı).");
        return ExitOk;
    }

    // ------------------------------------------------------------------ reapply-deletions

    /// <summary>
    /// Pilot izleme (Faz C): veritabanı, disk, takılı iş, belirsiz maliyet, bütçe ve kredi kontrolleri. YALNIZCA OKUR (<c>--confirm</c> gerekmez, hiçbir şey yazmaz).
    /// Çıkış kodu: 0 sorun yok, 10 uyarı var, 11 kritik. Cron ile çalıştırılıp çıkış koduna göre uyarı verilebilir (docs/19 §11).
    /// </summary>
    private static async Task<int> StatusAsync(Context c, CancellationToken ct)
    {
        var monitoring = c.Services.GetRequiredService<SanalProva.Infrastructure.Monitoring.PilotMonitoring>();
        var checks = await monitoring.CheckAsync(c.Db, ct);
        foreach (var check in checks)
        {
            var tag = check.Level switch
            {
                SanalProva.Infrastructure.Monitoring.MonitorLevel.Critical => "KRİTİK",
                SanalProva.Infrastructure.Monitoring.MonitorLevel.Warning => "UYARI ",
                _ => "TAMAM ",
            };
            await c.Out.WriteLineAsync($"[{tag}] {check.Name}: {check.Message}");
        }

        var worst = checks.Max(x => x.Level);
        return worst switch
        {
            SanalProva.Infrastructure.Monitoring.MonitorLevel.Critical => ExitMonitorCritical,
            SanalProva.Infrastructure.Monitoring.MonitorLevel.Warning => ExitMonitorWarning,
            _ => ExitOk,
        };
    }

    /// <summary>
    /// Yedekten geri yükleme sonrası (Pilot Faz A, KVKK): silme günlüğündeki oturumları, veritabanında yeniden belirmişse yeniden siler; veritabanında yoksa depoda kalmış
    /// dosya önekini siler. Günlük yedeklerin DIŞINDadır (bkz. <c>DeletionJournal</c>). İdempotenttir; <c>--confirm</c> yoksa yalnızca sayar. Günlükte yalnızca kimlikler bulunur.
    /// </summary>
    private static async Task<int> ReapplyDeletionsAsync(Context c, CancellationToken ct)
    {
        var path = c.Options.Journal ?? c.Journal.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return await UsageErrorAsync(c, "--journal <dosya> verin veya Retention:DeletionJournalPath yapılandırın.");
        }

        var (entries, skipped) = await SanalProva.Infrastructure.Retention.DeletionJournal.ReadAsync(path, ct);
        var sessions = entries.GroupBy(e => e.SessionId).Select(g => g.First()).ToList();
        var present = (await c.Db.TryOnSessions.IgnoreQueryFilters().AsNoTracking().Where(s => sessions.Select(e => e.SessionId).Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        await c.Out.WriteLineAsync($"PLAN: günlükte {sessions.Count} benzersiz silinmiş oturum ({skipped} bozuk satır atlandı); veritabanında yeniden belirmiş: {present.Count}; depo önekleri taranacak.");
        if (!c.Options.Confirm)
        {
            await c.Out.WriteLineAsync("KURU ÇALIŞMA: --confirm verilmedi, hiçbir şey silinmedi.");
            return ExitOk;
        }

        var purged = 0;
        var prefixes = 0;
        foreach (var entry in sessions)
        {
            if (present.Contains(entry.SessionId))
            {
                await c.Purger.PurgeAsync(c.Db, entry.SessionId, "Session.PurgedByRestoreReapply", null, ct, writeJournal: false);
                purged++;
            }
            else
            {
                // Satırı yok ama dosyaları geri yüklenmiş olabilir (eski depo yedeği): öneki sil (yoksa hiçbir şey olmaz)
                await c.Storage.DeletePrefixAsync(SanalProva.Infrastructure.Storage.StorageKeys.SessionPrefix(entry.OrganizationId, entry.StoreId, entry.SessionId), ct);
                prefixes++;
            }
        }

        await c.Out.WriteLineAsync($"TAMAM: {purged} oturum yeniden silindi, {prefixes} depo öneki temizlendi (denetim kayıtları yazıldı).");
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
        AppDbContext Db, UserManager<AppUser> Users, AccountSetupTokenService Tokens, AuditTrail Audit, SetupTokenOptions TokenOptions, CommandOptions Options, TextWriter Out,
        SanalProva.Infrastructure.Ai.BudgetSettings Budget, SanalProva.Infrastructure.Credits.CreditLedgerService Credits,
        SanalProva.Infrastructure.Retention.DeletionJournal Journal, IServiceProvider Services)
    {
        // Depo/silici yalnızca reapply-deletions'ta çözülür: diğer komutlar depo imzalama anahtarını gerektirmez
        public SanalProva.Infrastructure.Retention.SessionPurger Purger => Services.GetRequiredService<SanalProva.Infrastructure.Retention.SessionPurger>();
        public SanalProva.Infrastructure.Storage.IImageStorageService Storage => Services.GetRequiredService<SanalProva.Infrastructure.Storage.IImageStorageService>();
    }

    /// <summary>Sayısal bütçe seçeneği: <c>Set</c> seçenek verildi mi; <c>Value</c> null ise "none" (firma değerini sil).</summary>
    private readonly record struct BudgetArg(bool Set, decimal? Value);

    private sealed record CommandOptions(
        string? Error, string? Email, string? Name, string? Org, string? BaseUrl, bool Confirm, bool Additional, bool Enable, bool Disable,
        BudgetArg Daily = default, BudgetArg Monthly = default, BudgetArg Job = default, int? Grant = null, int? Adjust = null, string? Reference = null, string? Note = null, string? Journal = null)
    {
        public static CommandOptions Parse(string[] args)
        {
            string? email = null, name = null, org = null, baseUrl = null;
            bool confirm = false, additional = false, enable = false, disable = false;
            BudgetArg daily = default, monthly = default, job = default;
            int? grant = null, adjust = null;
            string? reference = null, note = null, journal = null;
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
                        case "--daily": daily = Money("--daily", Value("--daily")); break;
                        case "--monthly": monthly = Money("--monthly", Value("--monthly")); break;
                        case "--job": job = Money("--job", Value("--job")); break;
                        case "--grant": grant = Credits("--grant", Value("--grant")); break;
                        case "--adjust": adjust = Credits("--adjust", Value("--adjust")); break;
                        case "--ref": reference = Value("--ref").Trim(); break;
                        case "--note": note = Value("--note").Trim(); break;
                        case "--journal": journal = Value("--journal").Trim(); break;
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

            if (note is not null && note.Length > 200)
            {
                return Fail("--note en fazla 200 karakter olmalı.");
            }

            return new CommandOptions(null, email, name, org, baseUrl, confirm, additional, enable, disable, daily, monthly, job, grant, adjust, reference, note, journal);

            static CommandOptions Fail(string message) => new(message, null, null, null, null, false, false, false, false);
        }

        /// <summary>"none" = firma değerini sil; aksi halde 0–100000 USD, en çok 4 ondalık (nokta ayracı).</summary>
        private static BudgetArg Money(string flag, string text)
        {
            if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            {
                return new BudgetArg(true, null);
            }

            if (!decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0 || value > 100_000 || decimal.Round(value, 4) != value)
            {
                throw new FormatException($"{flag} değeri 0–100000 arasında, en çok 4 ondalıklı bir sayı (nokta ayraçlı) veya 'none' olmalı.");
            }

            return new BudgetArg(true, value);
        }

        /// <summary>Tam sayı kredi: en çok 1.000.000 (mutlak); işaretli olabilir (düzeltme).</summary>
        private static int Credits(string flag, string text)
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value) || value == 0 || Math.Abs(value) > 1_000_000)
            {
                throw new FormatException($"{flag} değeri sıfırdan farklı, en çok 1000000 mutlak değerli bir tam sayı olmalı.");
            }

            return value;
        }

        private static bool IsEmail(string value) =>
            value.Length <= 254 && MailAddress.TryCreate(value, out var address) && string.Equals(address.Address, value, StringComparison.Ordinal);
    }
}
