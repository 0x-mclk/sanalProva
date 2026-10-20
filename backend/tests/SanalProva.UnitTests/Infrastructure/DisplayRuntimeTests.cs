using Microsoft.Extensions.Options;
using SanalProva.Domain.Display;
using SanalProva.Infrastructure.Displays;

namespace SanalProva.UnitTests.Infrastructure;

public sealed class DisplayCredentialsTests
{
    [Fact]
    public void Uretilen_kod_alti_hanelidir()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.Matches("^[0-9]{6}$", DisplayCredentials.NewCode());
        }
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData("123 456", "123456")]
    [InlineData(" 123-456 ", "123456")]
    [InlineData("000001", "000001")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12a456", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Kod_normalize_edilir(string? input, string? expected) => Assert.Equal(expected, DisplayCredentials.NormalizeCode(input));

    [Fact]
    public void Kod_ozeti_anahtara_baglidir_ve_kodu_aciga_cikarmaz()
    {
        var a = DisplayCredentials.HashCode("123456", "anahtar-1");
        Assert.Equal(a, DisplayCredentials.HashCode("123456", "anahtar-1"));
        Assert.NotEqual(a, DisplayCredentials.HashCode("123456", "anahtar-2"));
        Assert.NotEqual(a, DisplayCredentials.HashCode("123457", "anahtar-1"));
        Assert.DoesNotContain("123456", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Gizli_degerler_benzersiz_ve_yeterince_uzundur()
    {
        var secrets = Enumerable.Range(0, 100).Select(_ => DisplayCredentials.NewSecret()).ToList();
        Assert.Equal(100, secrets.Distinct().Count());
        Assert.All(secrets, s => Assert.True(s.Length >= 43)); // 256 bit, base64url
        Assert.All(secrets, s => Assert.Matches("^[A-Za-z0-9_-]+$", s));
        Assert.Equal(64, DisplayCredentials.HashSecret(secrets[0]).Length);
    }
}

public sealed class DisplayPresenceTests
{
    [Fact]
    public void Ilk_baglanti_cevrimici_son_kopus_cevrimdisi_yapar()
    {
        var presence = new DisplayPresence();
        var id = Guid.NewGuid();

        Assert.False(presence.IsOnline(id));
        Assert.True(presence.Connected(id));
        Assert.False(presence.Connected(id)); // yeniden bağlanma: yeni bağlantı eskisi kapanmadan geldi
        Assert.True(presence.IsOnline(id));
        Assert.False(presence.Disconnected(id)); // hâlâ bir bağlantı var: çevrimdışı olmadı
        Assert.True(presence.IsOnline(id));
        Assert.True(presence.Disconnected(id));
        Assert.False(presence.IsOnline(id));
        Assert.False(presence.Disconnected(id)); // fazladan kapanış zararsız
    }

    [Fact]
    public void Cihazlar_birbirinden_bagimsizdir()
    {
        var presence = new DisplayPresence();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        presence.Connected(a);
        Assert.False(presence.IsOnline(b));
        presence.Forget(a);
        Assert.False(presence.IsOnline(a));
    }
}

public sealed class ClaimAttemptLimiterTests
{
    private static (ClaimAttemptLimiter Limiter, ManualTimeProvider Clock) Create()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        return (new ClaimAttemptLimiter(clock, Options.Create(new DisplaySettings { ClaimMaxFailures = 3, ClaimLockoutMinutes = 10 })), clock);
    }

    [Fact]
    public void Sinira_ulasinca_kilitlenir_sure_dolunca_acilir()
    {
        var (limiter, clock) = Create();
        var user = Guid.NewGuid();

        limiter.RecordFailure(user);
        limiter.RecordFailure(user);
        Assert.False(limiter.IsLocked(user));
        limiter.RecordFailure(user);
        Assert.True(limiter.IsLocked(user));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(limiter.IsLocked(user));
    }

    [Fact]
    public void Basarili_eslestirme_sayaci_sifirlar_ve_kullanicilar_ayridir()
    {
        var (limiter, _) = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            limiter.RecordFailure(a);
        }

        Assert.True(limiter.IsLocked(a));
        Assert.False(limiter.IsLocked(b));
        limiter.Reset(a);
        Assert.False(limiter.IsLocked(a));
    }
}

public sealed class DisplayStateReasonTests
{
    private static DisplayState State(DisplayMode mode) => new() { Mode = mode, ImageIds = mode == DisplayMode.Idle ? [] : [Guid.NewGuid()] };

    [Theory]
    [InlineData(DisplayMode.Idle, DisplayMode.Single, DisplayStateReason.ImageSent)]
    [InlineData(DisplayMode.Single, DisplayMode.Single, DisplayStateReason.ImageChanged)]
    [InlineData(DisplayMode.Single, DisplayMode.Compare, DisplayStateReason.CompareOpened)]
    [InlineData(DisplayMode.Idle, DisplayMode.Compare, DisplayStateReason.CompareOpened)]
    [InlineData(DisplayMode.Compare, DisplayMode.Compare, DisplayStateReason.ImageChanged)]
    [InlineData(DisplayMode.Compare, DisplayMode.Single, DisplayStateReason.CompareClosed)]
    [InlineData(DisplayMode.Single, DisplayMode.Idle, DisplayStateReason.Cleared)]
    [InlineData(DisplayMode.Compare, DisplayMode.Idle, DisplayStateReason.Cleared)]
    public void Neden_onceki_ve_yeni_moddan_cikar(DisplayMode previous, DisplayMode next, DisplayStateReason expected) =>
        Assert.Equal(expected, DisplayStateService.ReasonFor(State(previous), State(next)));
}
