using System.Net;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>İstekleri kaydeden ve hazır yanıtları sırayla dönen sahte HTTP işleyici. Gerçek ağ çağrısı yapılmaz.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public StubHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public StubHandler Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers) =>
        Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method, request.RequestUri!.AbsolutePath, body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase)));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"Beklenmeyen istek: {request.Method} {request.RequestUri}");
        }

        return _responses.Count == 1 ? _responses.Peek()(request) : _responses.Dequeue()(request);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    public JsonDocument BodyJson() => JsonDocument.Parse(Body ?? "{}");
}

internal static class AiTestImages
{
    /// <summary>Gerçek kişi içermeyen yapay görsel (rastgele renkli kareler).</summary>
    public static byte[] Jpeg(int width, int height, int seed = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(235, 225, 210));
            using var paint = new SKPaint();
            var random = new Random(seed);
            for (var i = 0; i < 120; i++)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawRect(random.Next(width), random.Next(height), random.Next(10, Math.Max(11, width / 6)), random.Next(10, Math.Max(11, height / 6)), paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    public static byte[] Flat(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(10, 10, 10));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }
}
