namespace SanalProva.Domain.Common;

/// <summary>Tüm kalıcı varlıkların tabanı. Kimlikler UUIDv7 (zamana göre sıralı) olarak uygulamada üretilir.</summary>
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Bir firmaya (kiracıya) ait kayıt. Global query filter ile izole edilir.</summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; }
}

/// <summary>Bir şubeye ait kayıt (prova oturumu, TV cihazı vb.).</summary>
public interface IStoreOwned : ITenantOwned
{
    Guid StoreId { get; }
}

/// <summary>
/// Bir prova oturumuna bağlı kayıt (fotoğraf, onay, seçim, sonuç görseli). Bu tablolarda şube sütunu yoktur: şube kapsamı <b>yapısal olarak oturum üzerinden</b> uygulanır
/// (EF filtresi, oturumun kendi firma+şube filtresinden geçer). Böylece her uç noktanın şubeyi elle birleştirmesine gerek kalmaz.
/// </summary>
public interface ISessionOwned : ITenantOwned
{
    Guid SessionId { get; }
}

/// <summary>Bir kıyafet seçimine bağlı kayıt (seçim kalemi): şube kapsamı seçim → oturum zinciriyle uygulanır.</summary>
public interface ISelectionOwned : ITenantOwned
{
    Guid SelectionId { get; }
}

/// <summary>Ürün ve müşteri fotoğraflarının çekim açısı. 360° desteğine hazırlık.</summary>
public enum ImageAngle
{
    Front,
    Back,
    Left,
    Right,
    Detail,
}
