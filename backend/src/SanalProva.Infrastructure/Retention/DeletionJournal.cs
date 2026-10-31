using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SanalProva.Infrastructure.Retention;

/// <summary>Silme günlüğü satırı: yalnızca kimlikler ve zaman; kişisel veri (ad, fotoğraf, e-posta) YOK.</summary>
public sealed record DeletionJournalEntry(Guid SessionId, Guid OrganizationId, Guid StoreId, DateTimeOffset At, string Reason);

/// <summary>
/// Silme günlüğü (Pilot Faz A, KVKK): <see cref="SessionPurger"/> bir oturumu silmeden ÖNCE buraya kimliğini ekler. Günlük veritabanı yedeğinin DIŞINDA, ayrı bir birimde durur;
/// böylece eski bir yedek geri yüklenip silinmiş kişisel veri veritabanında/depoda yeniden belirdiğinde <c>platform reapply-deletions</c> günlükten bu oturumları yeniden siler.
/// Biçim: satır başına bir JSON (JSONL), yalnızca ekleme. Önce-yaz (niyet): silme yarıda kalsa bile kayıt vardır; fazladan kayıt zararsızdır (silinmiş oturum yeniden silinir = hiçbir şey).
/// Günlük yazılamazsa silme DURMAZ (müşteri verisinin silinmesi önceliklidir) ama hata günlüğe yazılır ve izleme kontrol listesinde uyarı olarak izlenir.
/// <c>Retention:DeletionJournalPath</c> boşsa özellik kapalıdır (geliştirme/test); üretimde zorunludur (<c>ProductionStartupValidator</c>).
/// </summary>
public sealed class DeletionJournal(IOptions<RetentionSettings> settings, ILogger<DeletionJournal> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public bool Enabled => !string.IsNullOrWhiteSpace(settings.Value.DeletionJournalPath);

    public string? Path => Enabled ? settings.Value.DeletionJournalPath : null;

    public async Task RecordAsync(DeletionJournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Enabled)
        {
            return;
        }

        try
        {
            var path = settings.Value.DeletionJournalPath!;
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(entry, Json) + "\n";
            await WriteLock.WaitAsync(ct);
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line), ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                WriteLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Silme önceliklidir: günlük yazılamıyorsa işlem sürer; operatör bu hatayı izlemelidir (docs/19 §izleme)
            logger.LogError(ex, "Silme günlüğüne yazılamadı (silme sürdürüldü): {SessionId}", entry.SessionId);
        }
    }

    /// <summary>Günlüğü okur; bozuk satırlar atlanır (sayılır). Dosya yoksa boş döner.</summary>
    public static async Task<(IReadOnlyList<DeletionJournalEntry> Entries, int Skipped)> ReadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = new List<DeletionJournalEntry>();
        var skipped = 0;
        if (!File.Exists(path))
        {
            return (entries, skipped);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<DeletionJournalEntry>(line, Json);
                if (entry is null || entry.SessionId == Guid.Empty || entry.OrganizationId == Guid.Empty)
                {
                    skipped++;
                    continue;
                }

                entries.Add(entry);
            }
            catch (JsonException)
            {
                skipped++;
            }
        }

        return (entries, skipped);
    }
}
