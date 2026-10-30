using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Agreement;

/// <summary>Güncel hizmet sözleşmesi sürümünün kaynağı. Tek uygulama sabit belgeyi verir; testler yeni sürüm senaryosu için değiştirir.</summary>
public interface IServiceAgreementSource
{
    ServiceAgreementDocument Current { get; }
}

public sealed class StaticServiceAgreementSource : IServiceAgreementSource
{
    public ServiceAgreementDocument Current => ServiceAgreement.Document;
}

/// <summary>
/// Uç noktanın firma hizmet sözleşmesi KABUL EDİLMEDEN de çalışacağını belirtir. <b>Varsayılan zorlamadır</b>: kimlik doğrulaması isteyen her uç nokta,
/// bu işareti açıkça taşımadıkça sözleşme kabul edilene kadar <c>403 AGREEMENT_ACCEPTANCE_REQUIRED</c> alır. Yeni bir uç nokta unutulsa bile açık kalamaz.
/// İşaret yalnızca giriş/çıkış, sözleşmeyi görüntüleme/kabul etme, platform yönetimi ve TV cihazı gibi gerekçeli durumlarda kullanılır
/// (liste mimari testle sabitlenmiştir).
/// </summary>
public sealed record AgreementExemptMetadata(string Reason);

public static class AgreementExtensions
{
    public static TBuilder AllowWithoutAgreement<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return builder.WithMetadata(new AgreementExemptMetadata(reason));
    }

    public static IApplicationBuilder UseAgreementEnforcement(this IApplicationBuilder app) => app.UseMiddleware<AgreementEnforcementMiddleware>();
}

/// <summary>
/// "Bu firma güncel sözleşme sürümünü kabul etti mi?" Yalnızca <b>olumlu</b> sonuç önbelleğe alınır: kabul kaydı yalnızca eklenir (silinmez/güncellenmez), bu yüzden
/// önbellek bayatlayamaz; yeni sürüm farklı anahtardır. Olumsuz sonuç her seferinde veritabanından okunur (kabul eden Owner'ın hemen açması için).
/// </summary>
public sealed class AgreementGate(IServiceAgreementSource source)
{
    private readonly ConcurrentDictionary<(Guid OrganizationId, string Version), bool> _accepted = new();

    public ServiceAgreementDocument Current => source.Current;

    public async Task<bool> IsAcceptedAsync(AppDbContext db, Guid organizationId, CancellationToken ct)
    {
        var version = source.Current.Version;
        if (_accepted.ContainsKey((organizationId, version)))
        {
            return true;
        }

        var accepted = await db.OrganizationAgreementAcceptances.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(a => a.OrganizationId == organizationId && a.AgreementVersion == version, ct);
        if (accepted)
        {
            _accepted[(organizationId, version)] = true;
        }

        return accepted;
    }

    public void MarkAccepted(Guid organizationId, string version) => _accepted[(organizationId, version)] = true;
}

/// <summary>
/// Sözleşme zorlaması: istek <b>bağlama ve filtrelerden önce</b> reddedilir (gövde/CSRF/yetki hatası sırası fark etmez). Kapsam: kimliği doğrulanmış ve
/// <see cref="AgreementExemptMetadata"/> taşımayan tüm uç noktalar (REST + hub). Fail-closed: firma kimliği olmayan kimliklendirilmiş istek de reddedilir.
/// </summary>
public sealed class AgreementEnforcementMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "AGREEMENT_ACCEPTANCE_REQUIRED";

    public async Task InvokeAsync(HttpContext context, AgreementGate gate, AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        if (endpoint is not null && Enforced(endpoint) && context.User.Identity?.IsAuthenticated == true)
        {
            var organizationId = context.User.GetOrganizationId();
            if (organizationId is null || !await gate.IsAcceptedAsync(db, organizationId.Value, context.RequestAborted))
            {
                await ApiProblems.Create(StatusCodes.Status403Forbidden, ErrorCode, "Hizmet sözleşmesi kabul edilene kadar bu işlem yapılamaz. Firma sahibi sözleşmeyi kabul etmelidir.")
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    /// <summary>Kimlik doğrulaması isteyen (<see cref="IAuthorizeData"/>) ve muaf işareti olmayan uç nokta.</summary>
    public static bool Enforced(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 && endpoint.Metadata.GetMetadata<AgreementExemptMetadata>() is null;
    }
}
