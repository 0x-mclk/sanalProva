using SanalProva.Domain.Common;

namespace SanalProva.Domain.Tenancy;

/// <summary>
/// Firmanın (Owner aracılığıyla) sürümlü hizmet sözleşmesini kabul kaydı (M5.1). Müşterinin KVKK onayından (<c>ConsentRecord</c>)
/// tamamen ayrıdır: biri diğerinin yerine geçmez. Yalnızca ekleme (append-only): <c>sanalprova_app</c> için <c>UPDATE</c>/<c>DELETE</c>
/// veritabanında reddedilir (<c>scripts/mssql/harden-agreement-acceptances.sql</c>), aynı <c>ai_cost_reconciliations</c> deseniyle.
/// </summary>
public sealed class OrganizationAgreementAcceptance : ITenantOwned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid OrganizationId { get; init; }

    /// <summary>Kabul eden kullanıcı (yalnızca Owner). Her zaman <see cref="OrganizationId"/> ile aynı firmada.</summary>
    public Guid UserId { get; init; }

    /// <summary>Sözleşme metninin sürümü (<c>ServiceAgreement.Version</c> ile aynı biçim, ör. "2026-11-v1").</summary>
    public required string AgreementVersion { get; init; }

    /// <summary>Kabul edilen metnin SHA-256'sı: metin sonradan değişirse kabul kaydı hangi metne ait olduğunu kanıtlar.</summary>
    public required byte[] TextSha256 { get; init; }

    public DateTimeOffset AcceptedAt { get; init; }

    /// <summary>Denetim amaçlı IP hash'i (ham IP saklanmaz).</summary>
    public string? IpHash { get; init; }
}
