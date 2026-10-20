namespace SanalProva.Infrastructure.Tenancy;

/// <summary>
/// İsteğin hangi firma (kiracı) ve şube adına çalıştığı. Sorgu filtreleri bu değerleri sorgu anında okur,
/// bu yüzden DbContext oluşturulduktan sonra (ara katman / endpoint filtresi) atanabilirler.
/// <list type="bullet">
/// <item>Firma: oturum çerezindeki claim'den (istemci firma kimliği göndermez).</item>
/// <item>Şube: <c>X-Store-Id</c> başlığı, kullanıcının o şubedeki rolü doğrulandıktan sonra.</item>
/// </list>
/// Firma yoksa (anonim) kiracıya ait hiçbir kayıt görünmez. Şube yoksa şubeye ait kayıtlar firma içinde sınırsızdır;
/// şube uç noktaları bu nedenle her zaman şube erişim filtresinden geçer.
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    Guid? StoreId { get; }
}

/// <summary>İstek kapsamında doldurulan bağlam (ASP.NET Core ara katmanı ve worker kapsamı kullanır).</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? OrganizationId { get; set; }
    public Guid? StoreId { get; set; }
}

/// <summary>Kiracısız bağlam: anonim istekler ve tasarım zamanı (migration) için.</summary>
public sealed class NoTenantContext : ITenantContext
{
    public static readonly NoTenantContext Instance = new();
    public Guid? OrganizationId => null;
    public Guid? StoreId => null;
}

/// <summary>Testler için sabit kiracı/şube.</summary>
public sealed class FixedTenantContext(Guid organizationId, Guid? storeId = null) : ITenantContext
{
    public Guid? OrganizationId { get; } = organizationId;
    public Guid? StoreId { get; } = storeId;
}
