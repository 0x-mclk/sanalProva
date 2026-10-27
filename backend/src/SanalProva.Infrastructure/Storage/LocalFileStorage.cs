using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SanalProva.Infrastructure.Storage;

/// <summary>Geliştirme ve tek sunucu kurulumu için yerel disk deposu.</summary>
public sealed class LocalFileStorage : IImageStorageService
{
    private readonly string _root;
    private readonly FileUrlSigner _signer;

    public LocalFileStorage(IOptions<StorageOptions> options, IHostEnvironment environment, FileUrlSigner signer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.LocalRoot));
        _signer = signer;
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Önce geçici dosyaya yaz, sonra taşı: yarım dosya okunmasın
        var temp = path + ".tmp";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(file, ct);
        }

        File.Move(temp, path, overwrite: true);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredFolder>> ListSessionFoldersAsync(CancellationToken ct)
    {
        var result = new List<StoredFolder>();
        if (!Directory.Exists(_root))
        {
            return Task.FromResult<IReadOnlyList<StoredFolder>>(result);
        }

        // {kök}/{firma}/{şube}/sessions/{oturum}
        foreach (var organization in Directory.EnumerateDirectories(_root))
        {
            foreach (var store in Directory.EnumerateDirectories(organization))
            {
                var sessions = Path.Combine(store, "sessions");
                if (!Directory.Exists(sessions))
                {
                    continue;
                }

                foreach (var folder in Directory.EnumerateDirectories(sessions))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var sessionId))
                    {
                        continue;
                    }

                    var last = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();
                    var prefix = string.Join('/', Path.GetRelativePath(_root, folder).Split(Path.DirectorySeparatorChar)) + "/";
                    result.Add(new StoredFolder(prefix, sessionId, new DateTimeOffset(last, TimeSpan.Zero)));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<StoredFolder>>(result);
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        var path = ResolvePath(prefix.TrimEnd('/'));
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    public string GetReadUrl(string key, TimeSpan ttl)
    {
        _ = ResolvePath(key);
        return _signer.CreateUrl(key, ttl);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var probe = Path.Combine(_root, $".health-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string ResolvePath(string key)
    {
        if (!StorageKeys.IsValid(key.TrimEnd('/')))
        {
            throw new ArgumentException("Geçersiz depolama anahtarı.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Depolama anahtarı kök klasörün dışına çıkamaz.", nameof(key));
        }

        return full;
    }
}
