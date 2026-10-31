namespace SanalProva.Domain.Audit;

/// <summary>Denetim kaydı. Ham IP ve kişisel veri içermez.</summary>
public sealed class AuditLog
{
    public long Id { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid? StoreId { get; init; }
    public Guid? UserId { get; init; }
    public Guid? DeviceId { get; init; }

    /// <summary>Örn. "Session.Created", "Display.Assigned", "Session.Purged".</summary>
    public required string Action { get; init; }

    public required string EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? IpHash { get; init; }

    /// <summary>
    /// Kısa, SIR İÇERMEYEN izlenebilirlik bilgisi (≤300 karakter). Operatör komutlarında: kim (işletim sistemi kullanıcısı@makine), hangi komut ve hangi hedef/miktar/referans.
    /// Token, parola, e-posta, bağlantı veya serbest metin notu ASLA yazılmaz.
    /// </summary>
    public string? Detail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
