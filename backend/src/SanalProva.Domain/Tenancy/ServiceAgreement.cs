using System.Security.Cryptography;
using System.Text;

namespace SanalProva.Domain.Tenancy;

/// <summary>Sürümlü hizmet sözleşmesi belgesi. <see cref="TextSha256"/>, kabul edilen metnin değişmediğini kanıtlar (kabul kaydına yazılır).</summary>
public sealed record ServiceAgreementDocument(string Version, string Title, string Text)
{
    /// <summary>Metnin SHA-256 özeti (UTF-8, satır sonları <c>\n</c>'e normalize edilmiş: işletim sistemine göre değişmez).</summary>
    public byte[] TextSha256 { get; } = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(Text)));

    public string TextSha256Hex => Convert.ToHexStringLower(TextSha256);

    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

/// <summary>
/// İşletme (firma) hizmet sözleşmesi: Sanal Prova ile <b>işletme</b> arasındadır ve yalnızca firmanın Owner'ı kabul eder. Müşterinin fotoğraf/KVKK onayı
/// (<c>ConsentNotice</c>, <c>consent_records</c>) bundan TAMAMEN ayrıdır: farklı metin, farklı tablo, farklı uç nokta; biri diğerinin yerine geçmez.
/// <para>
/// <b>TASLAK:</b> metin hukuki incelemeden geçmeden canlıya çıkmaz (M5.1 D3). Metin değişince <see cref="Version"/> ARTIRILMALIDIR: mevcut kabuller eski sürüme ait
/// kalır ve firmalardan yeniden kabul istenir. Metni sürümü artırmadan değiştirmek birim testini kırar (özet sabitlenmiştir).
/// </para>
/// </summary>
public static class ServiceAgreement
{
    public const string Version = "2026-11-v1";

    public const string Title = "Sanal Prova Hizmet Sözleşmesi (TASLAK)";

    private const string Body = """
        TASLAK — Bu metin hukuki incelemeden geçmemiştir ve canlı kullanım için bağlayıcı değildir.

        1. Taraflar ve konu
        Bu sözleşme, Sanal Prova hizmetini sunan işletmeci ("Hizmet Sağlayıcı") ile hizmeti kendi mağazasında kullanan işletme ("İşletme") arasındadır.
        Sözleşmeyi İşletme adına yalnızca İşletme'nin Owner (sahip) yetkisine sahip kullanıcısı kabul edebilir.

        2. Hizmetin kapsamı
        Hizmet, mağaza personelinin çektiği müşteri fotoğrafı üzerinde yapay zekâ ile sanal kıyafet önizlemesi üretir. Üretilen görseller görsel bir
        örnektir; ürünün gerçek görünümü, rengi, kalıbı veya bedeni ile birebir aynı olduğu taahhüt edilmez. Müşteriye gösterilen renk önizlemeleri
        stokta olmayabilecek renkleri de içerebilir.

        3. İşletme'nin yükümlülükleri
        İşletme, kendi müşterilerinin kişisel verilerinin işlenmesinde veri sorumlusudur. Müşteriye aydınlatma metnini göstermek ve müşteri onayını
        almak İşletme'nin sorumluluğundadır; bu sözleşmenin kabulü müşteri onayının yerine geçmez. Reşit olmayanlar için veli onayı alınmalıdır.

        4. Veri saklama ve yurt dışına aktarım
        Müşteri görselleri varsayılan olarak 24 saat, müşterinin ek onayıyla en fazla 30 gün saklanır ve süre sonunda kalıcı olarak silinir.
        Görsel üretimi için yurt dışındaki yapay zekâ sağlayıcılarına aktarım yapılabilir; sağlayıcılar ve saklama koşulları İşletme'ye bildirilir.

        5. Ücretli yapay zekâ kullanımı
        Ücretli yapay zekâ kullanımı yeni İşletmeler için varsayılan olarak kapalıdır ve Hizmet Sağlayıcı tarafından ayrıca etkinleştirilir.
        Kullanım ücretleri ve kredi koşulları ayrı bir ekte belirlenir.

        6. Hesap güvenliği
        İşletme, kullanıcı hesaplarının ve kurulum bağlantılarının gizliliğinden sorumludur. Kurulum bağlantısı tek kullanımlıktır ve süreli geçerlidir.

        7. Yasaklı kullanım
        Hizmet; hukuka aykırı, başkasının haklarını ihlal eden veya rıza dışı içerik üretmek için kullanılamaz.

        8. Sorumluluğun sınırı
        Hizmet "olduğu gibi" sunulur. Sorumluluğun kapsamı ve sınırı hukuki inceleme sonrasında bu maddede belirlenecektir.

        9. Değişiklikler
        Sözleşme metni değiştiğinde yeni bir sürüm yayımlanır ve İşletme'nin Owner'ından yeniden kabul istenir. Yeni sürüm kabul edilene kadar
        mağaza işlemleri durdurulur; giriş ve sözleşmeyi görüntüleme/kabul etme çalışmaya devam eder.

        10. Yürürlük, fesih ve uygulanacak hukuk
        Bu madde hukuki inceleme sonrasında tamamlanacaktır.
        """;

    public static ServiceAgreementDocument Document { get; } = new(Version, Title, ServiceAgreementDocument.Normalize(Body));
}
