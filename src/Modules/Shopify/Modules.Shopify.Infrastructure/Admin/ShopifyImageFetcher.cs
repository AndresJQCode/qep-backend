using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Admin;

/// <summary>
/// Spec 2026-11-17 §5.3: baja la imagen destacada de un producto. Sólo <c>https://cdn.shopify.com</c> (la URL la arma Shopify,
/// pero viaja por la respuesta: un host distinto no se pide), sin redirecciones, 10 s y un tope de
/// <see cref="IShopifyImageFetcher.MaxBytes"/> leído en streaming (hasta el tope más uno: si llega, se corta). Sólo
/// <c>image/jpeg</c>, <c>image/png</c> o <c>image/webp</c>; el contenido lo vuelve a revisar Storage (magic bytes, cuarentena).
/// <para>Cualquier falla devuelve <c>null</c> y quien llama anota <c>image_failed</c>. El log nunca lleva la URL (su query trae
/// la firma del CDN), sólo el motivo.</para>
/// </summary>
internal sealed partial class ShopifyImageFetcher(IHttpClientFactory httpClientFactory, ILogger<ShopifyImageFetcher> logger)
    : IShopifyImageFetcher
{
    public const string HttpClientName = "shopify.cdn";

    private const string CdnHost = "cdn.shopify.com";

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify product image not fetched: {Reason}")]
    private static partial void LogNotFetched(ILogger logger, string reason);

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-shopify");
    }

    public async Task<ShopifyFetchedImage?> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri
            || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(url.IdnHost, CdnHost, StringComparison.OrdinalIgnoreCase)
            || !url.IsDefaultPort)
        {
            LogNotFetched(logger, "host_not_allowed");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogNotFetched(logger, $"http_{(int)response.StatusCode}");
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !AllowedTypes.Contains(contentType))
            {
                LogNotFetched(logger, "content_type_not_allowed");
                return null;
            }

            if (response.Content.Headers.ContentLength is > IShopifyImageFetcher.MaxBytes)
            {
                LogNotFetched(logger, "too_large");
                return null;
            }

            var content = await ReadCappedAsync(response.Content, cancellationToken);
            if (content is null)
            {
                LogNotFetched(logger, "too_large");
                return null;
            }

            return content.Length == 0 ? null : new ShopifyFetchedImage(content, contentType.ToLowerInvariant());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // Ni el mensaje ni la excepción: pueden traer la URL.
            LogNotFetched(logger, exception.GetType().Name);
            return null;
        }
    }

    // Lee hasta el tope más uno: si llega a pasarlo, la imagen es demasiado grande (un Content-Length ausente o mentiroso no
    // alcanza para saltarse el tope).
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > IShopifyImageFetcher.MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
