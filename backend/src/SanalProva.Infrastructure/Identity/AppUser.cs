using Microsoft.AspNetCore.Identity;

namespace SanalProva.Infrastructure.Identity;

/// <summary>Personel hesabı. Kullanıcı tek bir firmaya aittir; şube rolleri <c>user_store_roles</c> tablosundadır.</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser()
    {
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.NewGuid().ToString();
    }

    public Guid OrganizationId { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Ekran kilidi PIN'inin hash'i (Identity parola hasher'ı ile).</summary>
    public string? PinHash { get; set; }

    /// <summary>Ardışık hatalı PIN/parola sayısı (kilit açma). Başarıda sıfırlanır; sınıra ulaşınca oturumlar kapatılır (PinPolicy).</summary>
    public int PinFailedAttempts { get; set; }

    /// <summary>PIN'i siler ve sayacı sıfırlar: pasife alma ve parola sıfırlamada eski PIN yeni oturuma taşınmaz.</summary>
    public void ClearPin()
    {
        PinHash = null;
        PinFailedAttempts = 0;
    }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Platform (SaaS işletmecisi) yöneticisi. Mağaza rollerinden ayrıdır ve hiçbir API ile verilemez: yalnızca veritabanında/dağıtım betiğiyle ayarlanır.
    /// Her istekte veritabanından okunur (rol geri alınınca çerez süresini beklemez).
    /// </summary>
    public bool IsPlatformAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
