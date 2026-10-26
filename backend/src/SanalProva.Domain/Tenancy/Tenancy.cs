using SanalProva.Domain.Common;

namespace SanalProva.Domain.Tenancy;

/// <summary>Firma (kiracı). Katalog bu seviyede paylaşılır.</summary>
public sealed class Organization : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public OrganizationSettings Settings { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

/// <summary>Firma ayarları (JSON sütunu).</summary>
public sealed class OrganizationSettings
{
    /// <summary>Müşteri görsellerinin varsayılan saklama süresi (saat). Üst sınır <see cref="RetentionPolicy.MaxExtendedRetention"/>.</summary>
    public int DefaultRetentionHours { get; set; } = 24;

    /// <summary>Ek onayla uzatılmış saklama süresi (gün).</summary>
    public int ExtendedRetentionDays { get; set; } = 30;

    public int DailyGenerationQuota { get; set; } = 200;

    /// <summary>Katalog görselleri örnek (sahte) ise arayüz kartlarda "Örnek" rozeti gösterir. Yalnızca demo ortamında true.</summary>
    public bool IsDemoCatalog { get; set; }
    public int MaxGenerationsPerSession { get; set; } = 15;

    /// <summary>Hazır takım (FASHN) çözünürlüğü: "1k" veya "2k". null = sunucu yapılandırmasındaki varsayılan (balanced + 1k).</summary>
    public string? FashnResolution { get; set; }

    /// <summary>Tek ürün giydirmede varsayılan model anahtarı. null = sistem varsayılanı (Seedream 5 Flash).</summary>
    public string? DefaultTryOnModel { get; set; }

    /// <summary>Personelin kullanabileceği model anahtarları. null = eski olmayan tüm modeller.</summary>
    public List<string>? EnabledTryOnModels { get; set; }

    /// <summary>İzin verilen kaliteler, "model:kalite" biçiminde (ör. "gpt-image-2.5:medium"). Listede adı geçmeyen model için tüm kaliteler serbesttir.</summary>
    public List<string>? AllowedTryOnQualities { get; set; }

    /// <summary>Model başına varsayılan kalite, "model:kalite" biçiminde. Yoksa modelin sistem varsayılanı (GPT Image için medium).</summary>
    public List<string>? DefaultTryOnQualities { get; set; }

    /// <summary>Hazır takım (FASHN) üretim modu: "fast", "balanced" veya "quality". null = sunucu varsayılanı.</summary>
    public string? FashnGenerationMode { get; set; }
}

/// <summary>Şube. Prova oturumları ve TV cihazları şube seviyesindedir.</summary>
public sealed class Store : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public string Timezone { get; set; } = "Europe/Istanbul";
    public int? DailyGenerationQuota { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum StoreRole
{
    Staff,
    Manager,
    Owner,
}

/// <summary>Kullanıcının bir şubedeki rolü. Owner için her şubeye ayrı satır açılır.</summary>
public sealed class UserStoreRole : ITenantOwned
{
    public Guid UserId { get; init; }
    public Guid StoreId { get; init; }
    public Guid OrganizationId { get; init; }
    public StoreRole Role { get; set; }
}
