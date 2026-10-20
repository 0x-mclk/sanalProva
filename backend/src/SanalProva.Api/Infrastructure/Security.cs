using Microsoft.AspNetCore.Antiforgery;

namespace SanalProva.Api.Infrastructure;

public static class SecurityHeaders
{
    /// <summary>API yanıtları için temel güvenlik başlıkları. Frontend başlıkları (CSP) dağıtımda reverse proxy'de verilir.</summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            await next();
        });
}

/// <summary>
/// Çerezle kimlik doğrulanan, durum değiştiren isteklerde antiforgery başlığını (X-CSRF-TOKEN) doğrular.
/// Minimal API JSON uç noktaları otomatik doğrulanmadığı için grup seviyesinde uygulanır.
/// </summary>
public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public const string HeaderName = "X-CSRF-TOKEN";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
        {
            return await next(context);
        }

        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return Results.Problem(
                title: "Güvenlik doğrulaması başarısız. Sayfayı yenileyip tekrar deneyin.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "CSRF_INVALID" });
        }

        return await next(context);
    }
}
