using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M5.1b: <see cref="AccountSetupTokenService"/>'in veritabanına bağlı davranışı (üretim, doğrulama, atomik tüketim, iptal).
/// Her test kendi geçici laboratuvar veritabanında (<see cref="MigrationLab"/>) çalışır; <c>sanalprova_dev</c>'e dokunulmaz.
/// Saf kriptografi (token formatı, HMAC determinizmi/anahtar bağımlılığı) <c>SanalProva.UnitTests</c>'tedir.
/// </summary>
public sealed class AccountSetupTokenServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string TestKey = "test-amacli-anahtar-en-az-32-karakter-uzunlugunda";

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static AccountSetupTokenService Service(AppDbContext db, TimeProvider clock, string key = TestKey) =>
        new(db, Options.Create(new SetupTokenOptions { SetupTokenKey = key }), clock);

    private static async Task<(Guid UserId, Guid OrganizationId)> CreateUserAsync(RowFactory rows, CancellationToken ct)
    {
        var user = await rows.CreateAsync("users", null, ct);
        return ((Guid)user["id"]!, (Guid)user["organization_id"]!);
    }

    [Fact]
    public async Task Uretilen_token_dogrulanir_veritabaninda_yalnizca_ozeti_tutulur()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);

        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        string plainToken;
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            var service = Service(db, clock);
            var (token, entity) = await service.IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(72), null, Ct);
            plainToken = token;
            Assert.Equal(32, entity.TokenHash.Length);
            Assert.DoesNotContain(token, Convert.ToHexStringLower(entity.TokenHash), StringComparison.Ordinal);
        }

        // Düz token veritabanında hiçbir yerde yok: ham sorguyla aransa bile bulunmaz
        var rawMatch = await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM account_setup_tokens WHERE CAST(token_hash AS nvarchar(max)) LIKE N'%{plainToken}%'", ct: Ct);
        Assert.Equal(0, rawMatch);

        await using var verify = MigrationLab.OpenDb(lab.AppCs);
        Assert.Equal(SetupTokenValidity.Valid, await Service(verify, clock).ValidateAsync(plainToken, Ct));
    }

    [Fact]
    public async Task Tuketim_atomiktir_ikinci_tuketim_reddedilir()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        string plainToken;
        await using (var issueDb = MigrationLab.OpenDb(lab.AppCs))
        {
            (plainToken, _) = await Service(issueDb, clock).IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(72), null, Ct);
        }

        await using (var consumeDb = MigrationLab.OpenDb(lab.AppCs))
        {
            var result = await Service(consumeDb, clock).ConsumeAsync(plainToken, AccountSetupTokenPurpose.InitialPassword, Ct);
            Assert.Equal(SetupTokenConsumeStatus.Success, result.Status);
            Assert.Equal(userId, result.UserId);
            Assert.Equal(orgId, result.OrganizationId);
        }

        await using (var secondDb = MigrationLab.OpenDb(lab.AppCs))
        {
            var second = await Service(secondDb, clock).ConsumeAsync(plainToken, AccountSetupTokenPurpose.InitialPassword, Ct);
            Assert.Equal(SetupTokenConsumeStatus.Invalid, second.Status);
            Assert.Equal(SetupTokenValidity.Used, await Service(secondDb, clock).ValidateAsync(plainToken, Ct));
        }
    }

    [Fact]
    public async Task Eszamanli_tuketimde_tam_olarak_bir_istek_basarili_olur()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        string plainToken;
        await using (var issueDb = MigrationLab.OpenDb(lab.AppCs))
        {
            (plainToken, _) = await Service(issueDb, clock).IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(72), null, Ct);
        }

        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = MigrationLab.OpenDb(lab.AppCs);
            return await Service(db, clock).ConsumeAsync(plainToken, AccountSetupTokenPurpose.InitialPassword, Ct);
        }));

        Assert.Single(attempts, a => a.Status == SetupTokenConsumeStatus.Success);
        Assert.Equal(11, attempts.Count(a => a.Status == SetupTokenConsumeStatus.Invalid));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM account_setup_tokens WHERE used_at IS NOT NULL", ct: Ct));
    }

    [Fact]
    public async Task Suresi_dolan_token_tuketilemez()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));

        string plainToken;
        await using (var issueDb = MigrationLab.OpenDb(lab.AppCs))
        {
            (plainToken, _) = await Service(issueDb, clock).IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(1), null, Ct);
        }

        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        await using var db = MigrationLab.OpenDb(lab.AppCs);
        var service = Service(db, clock);
        Assert.Equal(SetupTokenValidity.Expired, await service.ValidateAsync(plainToken, Ct));
        Assert.Equal(SetupTokenConsumeStatus.Invalid, (await service.ConsumeAsync(plainToken, AccountSetupTokenPurpose.InitialPassword, Ct)).Status);
    }

    [Fact]
    public async Task Yeniden_uretim_onceki_acik_tokeni_iptal_eder_farkli_amac_etkilenmez()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        string firstToken, secondToken, resetToken;
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            var service = Service(db, clock);
            (firstToken, _) = await service.IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(72), null, Ct);
            (resetToken, _) = await service.IssueAsync(userId, orgId, AccountSetupTokenPurpose.PasswordReset, TimeSpan.FromHours(1), null, Ct);
            (secondToken, _) = await service.IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(72), null, Ct);
        }

        await using var verify = MigrationLab.OpenDb(lab.AppCs);
        var verifyService = Service(verify, clock);
        Assert.Equal(SetupTokenValidity.Revoked, await verifyService.ValidateAsync(firstToken, Ct));
        Assert.Equal(SetupTokenValidity.Valid, await verifyService.ValidateAsync(secondToken, Ct));
        Assert.Equal(SetupTokenValidity.Valid, await verifyService.ValidateAsync(resetToken, Ct)); // farklı amaç: etkilenmedi
        Assert.Equal(SetupTokenConsumeStatus.Invalid, (await verifyService.ConsumeAsync(firstToken, AccountSetupTokenPurpose.InitialPassword, Ct)).Status);
    }

    [Fact]
    public async Task Acikca_iptal_edilen_token_tuketilemez_yeni_token_uretmez()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        string plainToken;
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            (plainToken, _) = await Service(db, clock).IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(72), null, Ct);
        }

        await using (var revokeDb = MigrationLab.OpenDb(lab.AppCs))
        {
            var revoked = await Service(revokeDb, clock).RevokeOpenAsync(userId, AccountSetupTokenPurpose.InitialPassword, Ct);
            Assert.Equal(1, revoked);
        }

        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM account_setup_tokens WHERE revoked_at IS NOT NULL", ct: Ct));
        await using var verify = MigrationLab.OpenDb(lab.AppCs);
        Assert.Equal(SetupTokenValidity.Revoked, await Service(verify, clock).ValidateAsync(plainToken, Ct));
    }

    [Fact]
    public async Task Olmayan_token_bulunamadi_doner()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        await using var db = MigrationLab.OpenDb(lab.AppCs);
        var service = Service(db, new ManualClock(DateTimeOffset.UtcNow));
        Assert.Equal(SetupTokenValidity.NotFound, await service.ValidateAsync(AccountSetupTokenCredentials.NewToken(), Ct));
        Assert.Equal(SetupTokenConsumeStatus.Invalid, (await service.ConsumeAsync(AccountSetupTokenCredentials.NewToken(), AccountSetupTokenPurpose.InitialPassword, Ct)).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(73)]
    public async Task Gecersiz_sure_reddedilir(int hours)
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);

        await using var db = MigrationLab.OpenDb(lab.AppCs);
        var service = Service(db, new ManualClock(DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(hours), null, Ct));
    }

    [Fact]
    public async Task Yanlis_anahtarla_dogrulama_basarisiz_olur()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var (userId, orgId) = await CreateUserAsync(rows, Ct);
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        string plainToken;
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            (plainToken, _) = await Service(db, clock).IssueAsync(userId, orgId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(72), null, Ct);
        }

        await using var verify = MigrationLab.OpenDb(lab.AppCs);
        var wrongKeyService = Service(verify, clock, "farkli-bir-anahtar-en-az-32-karakter-uzunlugunda");
        Assert.Equal(SetupTokenValidity.NotFound, await wrongKeyService.ValidateAsync(plainToken, Ct));
    }
}
