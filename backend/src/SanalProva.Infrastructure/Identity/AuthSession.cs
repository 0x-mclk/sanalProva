namespace SanalProva.Infrastructure.Identity;

/// <summary>
/// Bir girişin (çerez oturumunun) sunucu tarafı durumu (Pilot öncesi kapanış): çerezdeki <c>sp_sid</c> claim'i bu satırın kimliğidir. Ekran kilidi SUNUCUDA bu satırda tutulur
/// (<see cref="LockedAt"/>): kilitli oturum, kilit açma/çıkış/kimlik bilgisi dışındaki kimlikli uç noktalarda <c>423 SCREEN_LOCKED</c> alır; sayfa yenileme, doğrudan API
/// çağrısı ve ikinci sekme çerezi aynı satırı paylaştığı için kilidi atlatamaz. <see cref="LastInteractionAt"/> istemci etkileşim sinyaliyle güncellenir; süre
/// (<c>Security:IdleLockSeconds</c> + tolerans) dolarsa istemci çalışmasa bile sunucu oturumu kilitli sayar. Satırda kişisel veri yoktur; çıkışta silinir.
/// Firma sütunu yoktur (kullanıcıya bağlıdır; kullanıcı gibi firma filtresi dışındadır).
/// </summary>
public sealed class AuthSession
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid UserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastInteractionAt { get; set; }

    public DateTimeOffset? LockedAt { get; set; }
}
