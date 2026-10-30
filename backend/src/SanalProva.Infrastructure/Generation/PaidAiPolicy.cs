using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Infrastructure.Generation;

/// <summary>
/// Firma düzeyinde ücretli AI kapısı (M5.1f): <see cref="OrganizationSettings.PaidAiEnabled"/>. Yeni firmalar için KAPALIDIR ve yalnızca operatör yetkisiyle açılır
/// (hiçbir firma/personel API'si bu alanı yazmaz; kaynak taraması testle sabit). İki yerde denetlenir: iş kabulünde (kuyruğa hiç girmez) ve sağlayıcı çağrısından hemen önce
/// <see cref="BudgetGuard.ReserveAsync"/> içinde (kuyrukta bekleyen iş sırasında kapatılmış olabilir). <b>Fail-closed</b>: firma/ayar okunamıyor, eksik veya bozuksa
/// "kapalı" sayılır. Mock sağlayıcı bu kapıya tabi değildir (ücret yok). Global kapılar (<c>UseMockProviders</c>, <c>AllowPaidCalls</c>, API anahtarı, bütçe) buna EK olarak aynen geçerlidir.
/// </summary>
public static class PaidAiPolicy
{
    public static bool IsEnabled(OrganizationSettings? settings) => settings is { PaidAiEnabled: true };

    /// <summary>Firma ayarını okuyup denetler. Veritabanı/bağlantı hataları çağırana yayılır (altyapı hatası "ücretli kapalı" demek değildir; çağıran işi ücretsiz yeniden dener); bozuk/eksik ayar kapalı sayılır.</summary>
    public static async Task<bool> IsEnabledForAsync(AppDbContext db, Guid organizationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == organizationId, ct);
            return IsEnabled(organization?.Settings);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DbException or DbUpdateException))
        {
            return false; // ayar JSON'u bozuk/okunamıyor: fail-closed
        }
    }
}
