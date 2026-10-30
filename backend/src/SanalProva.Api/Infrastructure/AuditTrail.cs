using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Audit;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Api.Infrastructure;

/// <summary>
/// Denetim kaydı. Ham IP veya kişisel veri yazılmaz; IP, gizli anahtarla karıştırılmış kısa özet olarak tutulur.
/// Kayıt değişiklik bağlamına eklenir, çağıran <c>SaveChanges</c> ile birlikte kaydeder.
/// </summary>
public sealed class AuditTrail(AppDbContext db, CurrentUser user, IHttpContextAccessor http, TimeProvider clock, IOptions<StorageOptions> options)
{
    public void Record(string action, string entityType, Guid? entityId)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = user.OrganizationId,
            StoreId = user.StoreId,
            UserId = user.UserId == Guid.Empty ? null : user.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            IpHash = HashIp(http.HttpContext?.Connection.RemoteIpAddress?.ToString()),
            CreatedAt = clock.GetUtcNow(),
        });
    }

    /// <summary>
    /// Başka bir firma adına denetim kaydı (platform yöneticisi işlemleri; M5.1) veya henüz oturumu olmayan anonim işlemler (şifre belirleme).
    /// Kayıt <paramref name="organizationId"/> firmasına yazılır; <paramref name="userId"/> bilgi amaçlıdır (FK yok). Kişisel veri/token yazılmaz.
    /// </summary>
    public void RecordFor(Guid organizationId, Guid? userId, string action, string entityType, Guid? entityId)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = organizationId,
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            IpHash = HashIp(http.HttpContext?.Connection.RemoteIpAddress?.ToString()),
            CreatedAt = clock.GetUtcNow(),
        });
    }

    /// <summary>İsteğin IP özeti (ham IP asla yazılmaz). Başka bir bağlamda denetim kaydı yazan servisler için.</summary>
    public string? RequestIpHash() => HashIp(http.HttpContext?.Connection.RemoteIpAddress?.ToString());

    private string? HashIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return null;
        }

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SigningKey), Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexStringLower(hash)[..32];
    }
}
