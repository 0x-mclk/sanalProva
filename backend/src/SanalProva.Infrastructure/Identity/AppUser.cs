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

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
