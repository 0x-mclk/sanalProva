using SanalProva.Domain.Common;

namespace SanalProva.Domain.Tenancy;

/// <summary>Firma (kiracı). Katalog bu seviyede paylaşılır.</summary>
public sealed class Organization : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public OrganizationSettings Settings { get; set; } = new();
    public bool IsActive { get; set; } = true;

    /// <summary>Firmayı oluşturan platform yöneticisi (M5.1). Bilinçli olarak FK yok (platform yöneticisi başka firmadadır).</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// Oluşturma isteğinin Idempotency-Key'i (M5.1): aynı anahtarla tekrar aynı sonucu döner, ama kurulum bağlantısı
    /// yalnızca ilk yanıtta gösterildiği için tekrarında bir daha gösterilmez (<see cref="CreationRequestHash"/> ile birlikte).
    /// </summary>
    public string? CreationRequestKey { get; set; }

    /// <summary>İstek gövdesinin özeti: aynı anahtarla farklı gövde gelirse <c>IDEMPOTENCY_KEY_REUSED</c> ile reddedilir.</summary>
    public byte[]? CreationRequestHash { get; set; }
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

    /// <summary>
    /// Firma ücretli AI kullanabilir mi (M5.1). Varsayılan <c>false</c>: yeni oluşturulan firmalar M5.4 kredi sistemi kurulana kadar
    /// ücretli AI'ya otomatik yetkili değildir. Yalnızca operatör komutuyla açılır; oluşturma API'si bu alanı ayarlayamaz.
    /// Gerçek (mock olmayan) modda iş kabulünde ve <c>BudgetGuard</c> sağlayıcı çağrısından önce ikinci kez denetlenir.
    /// </summary>
    public bool PaidAiEnabled { get; set; }

    /// <summary>
    /// Firma başına günlük (UTC günü) AI harcama sınırı, USD (M5.3). <c>null</c> = yalnızca platform üst sınırı (<c>Generation:Budget</c>) geçerli.
    /// Değer platform üst sınırını AŞAMAZ: etkin sınır ikisinin küçüğüdür. Yalnızca operatör komutuyla yazılır (<c>platform budget</c>).
    /// </summary>
    public decimal? DailyBudgetUsd { get; set; }

    /// <summary>Firma başına takvim ayı (UTC) AI harcama sınırı, USD. <c>null</c> = platform üst sınırı. Etkin sınır = min(platform, firma).</summary>
    public decimal? MonthlyBudgetUsd { get; set; }

    /// <summary>Tek bir işin (yeniden denemeler dahil) azami maliyeti, USD. <c>null</c> = platform üst sınırı. Etkin sınır = min(platform, firma).</summary>
    public decimal? MaxJobCostUsd { get; set; }
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
