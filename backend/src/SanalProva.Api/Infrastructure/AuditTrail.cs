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
