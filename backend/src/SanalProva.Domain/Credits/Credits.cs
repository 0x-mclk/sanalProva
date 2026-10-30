using SanalProva.Domain.Common;

namespace SanalProva.Domain.Credits;

/// <summary>
/// Kredi defteri hareket türü (M5.4). Bakiye iki sayıdır: <c>BalanceCredits</c> (kesinleşmiş) ve <c>ReservedCredits</c> (devam eden işler için bloke).
/// Kullanılabilir = Bakiye − Rezerve.
/// </summary>
public enum CreditEntryType
{
    /// <summary>Kredi yükleme (operatör veya ileride ödeme): bakiye +amount.</summary>
    Grant,

    /// <summary>Düzeltme (± amount): bakiye değişir; sonuç bakiye rezerve tutarın altına inemez.</summary>
    Adjustment,

    /// <summary>İş kabulünde ayırma: rezerve +amount (kullanılabilir −amount).</summary>
    Reserve,

    /// <summary>İş kredi tüketmeden bitti (kesin başarısızlık, hiç gönderilmemiş iptal, ücretlenmedi kararı): rezerve −amount.</summary>
    Release,

    /// <summary>Başarılı iş (veya ücretlendi kararı): rezerve −amount ve bakiye −amount.</summary>
    Consume,
}

/// <summary>Hareketin kaynağı. <see cref="Payment"/> M5.5 ödeme entegrasyonuna ayrılmıştır (bu aşamada hiçbir kod üretmez).</summary>
public enum CreditSource
{
    Operator,
    System,
    Payment,
}

/// <summary>
/// Firmanın kredi hesabı: her firmada tek satır. <b>Bakiye negatif olamaz</b> ve rezerve bakiyeyi aşamaz (veritabanı CHECK). Satır yalnızca
/// <c>CreditLedgerService</c> tarafından, firma başına uygulama kilidi altında ve aynı transaction'da bir defter girdisiyle birlikte değiştirilir.
/// </summary>
public sealed class CreditAccount : ITenantOwned
{
    public Guid OrganizationId { get; init; }

    public int BalanceCredits { get; set; }

    public int ReservedCredits { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int AvailableCredits => BalanceCredits - ReservedCredits;
}

/// <summary>
/// Değiştirilemez (append-only) kredi defteri girdisi. UPDATE/DELETE veritabanı tetikleyicisiyle reddedilir. Bakiye = Σ(Grant+Adjustment) − Σ(Consume);
/// rezerve = Σ(Reserve) − Σ(Release) − Σ(Consume) (tutarlılık testleriyle sabit). İş başına en fazla bir <c>Reserve</c> ve en fazla bir kapanış
/// (<c>Release</c> veya <c>Consume</c>) olabilir (filtreli benzersiz indeks): çift düşüm/çift iade veritabanında imkânsızdır.
/// <c>JobId</c> bilinçli olarak yabancı anahtar DEĞİLDİR: oturum silinince (KVKK) iş gider, mali kayıt kalır; kişisel veri içermez.
/// </summary>
public sealed class CreditLedgerEntry : ITenantOwned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid OrganizationId { get; init; }

    public CreditEntryType EntryType { get; init; }

    /// <summary>Grant/Reserve/Release/Consume için pozitif; Adjustment için işaretli (sıfır olamaz).</summary>
    public int Amount { get; init; }

    public Guid? JobId { get; init; }

    /// <summary>
    /// Grant/Adjustment için zorunlu, firma içinde benzersiz işlem referansı (idempotency): aynı referansla tekrar gelen yükleme ikinci kez uygulanmaz.
    /// M5.5'te ödeme sağlayıcısının işlem kimliği de buraya yazılır.
    /// </summary>
    public string? Reference { get; init; }

    public CreditSource Source { get; init; }

    public string? Note { get; init; }

    /// <summary>Reserve girdisinde işe uygulanan tarife satırının anahtarı (tarife sonradan değişse de neyin uygulandığı kayıtlıdır).</summary>
    public string? TariffKey { get; init; }

    public int BalanceAfter { get; init; }

    public int ReservedAfter { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
