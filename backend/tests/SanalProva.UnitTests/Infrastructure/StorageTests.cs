using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.UnitTests.Infrastructure;

public sealed class FileUrlSignerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Key = "0192abcd/0192ef01/sessions/0192aaaa/0192bbbb.jpg";

    private static (FileUrlSigner Signer, ManualTimeProvider Clock) Create()
    {
        var clock = new ManualTimeProvider(Now);
        var options = Options.Create(new StorageOptions { SigningKey = new string('k', 32) });
        return (new FileUrlSigner(options, clock), clock);
    }

    private static (long Exp, string Sig) Parse(string url)
    {
        var query = url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        return (long.Parse(query["exp"], System.Globalization.CultureInfo.InvariantCulture), query["sig"]);
    }

    [Fact]
    public void Gecerli_imza_dogrulanir()
    {
        var (signer, _) = Create();
        var url = signer.CreateUrl(Key, TimeSpan.FromMinutes(5));
        var (exp, sig) = Parse(url);

        Assert.StartsWith(FileUrlSigner.RoutePrefix + Key + "?", url, StringComparison.Ordinal);
        Assert.True(signer.Validate(Key, exp, sig));
    }

    [Fact]
    public void Suresi_dolan_imza_reddedilir()
    {
        var (signer, clock) = Create();
        var (exp, sig) = Parse(signer.CreateUrl(Key, TimeSpan.FromMinutes(5)));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.False(signer.Validate(Key, exp, sig));
    }

    [Fact]
    public void Baska_dosya_veya_degistirilmis_sure_icin_imza_gecersizdir()
    {
        var (signer, _) = Create();
        var (exp, sig) = Parse(signer.CreateUrl(Key, TimeSpan.FromMinutes(5)));

        Assert.False(signer.Validate(Key.Replace("bbbb", "cccc", StringComparison.Ordinal), exp, sig));
        Assert.False(signer.Validate(Key, exp + 3600, sig));
        Assert.False(signer.Validate(Key, exp, null));
    }
}

public sealed class StorageKeysTests
{
    [Theory]
    [InlineData("0192abcd/catalog/0192ef01/0192aaaa.jpg", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("0192abcd/../../secret.jpg", false)]
    [InlineData("/absolute/path.jpg", false)]
    [InlineData("C:\\windows\\file.jpg", false)]
    [InlineData("", false)]
    public void Anahtar_dogrulamasi_yol_gecisini_engeller(string key, bool expected) =>
        Assert.Equal(expected, StorageKeys.IsValid(key));

    [Fact]
    public void Oturum_dosyalari_ortak_onek_altinda_toplanir()
    {
        Guid org = Guid.CreateVersion7(), store = Guid.CreateVersion7(), session = Guid.CreateVersion7();

        var key = StorageKeys.SessionFile(org, store, session, Guid.CreateVersion7(), ".JPG");

        Assert.StartsWith(StorageKeys.SessionPrefix(org, store, session), key, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", key, StringComparison.Ordinal);
        Assert.True(StorageKeys.IsValid(key));
    }

    [Fact]
    public void Desteklenmeyen_uzanti_reddedilir() =>
        Assert.Throws<ArgumentException>(() => StorageKeys.CatalogFile(Guid.Empty, Guid.Empty, Guid.Empty, "exe"));
}

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sanalprova-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        var options = Options.Create(new StorageOptions { LocalRoot = _root, SigningKey = new string('k', 32) });
        _storage = new LocalFileStorage(options, new TestHostEnvironment(), new FileUrlSigner(options, TimeProvider.System));
    }

    [Fact]
    public async Task Kaydet_oku_ve_onekle_sil()
    {
        Guid org = Guid.CreateVersion7(), store = Guid.CreateVersion7(), session = Guid.CreateVersion7();
        var key = StorageKeys.SessionFile(org, store, session, Guid.CreateVersion7(), "jpg");
        var ct = TestContext.Current.CancellationToken;

        await _storage.SaveAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("gorsel")), "image/jpeg", ct);
        await using (var read = await _storage.OpenReadAsync(key, ct))
        {
            Assert.NotNull(read);
            using var reader = new StreamReader(read);
            Assert.Equal("gorsel", await reader.ReadToEndAsync(ct));
        }

        await _storage.DeletePrefixAsync(StorageKeys.SessionPrefix(org, store, session), ct);

        Assert.Null(await _storage.OpenReadAsync(key, ct));
        Assert.True(await _storage.IsHealthyAsync(ct));
    }

    [Fact]
    public async Task Kok_disina_cikan_anahtar_reddedilir() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.OpenReadAsync("../outside.jpg", TestContext.Current.CancellationToken));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
