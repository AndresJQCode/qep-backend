using Microsoft.AspNetCore.Http;

namespace Modules.Shopify.Api;

/// <summary><see cref="Body"/> con el cuerpo y 200, o <c>null</c> con el status con el que hay que responder.</summary>
internal readonly record struct ShopifyWebhookBodyReadResult(byte[]? Body, int Status);

/// <summary>
/// Spec 2026-10-15 §5.1: el cuerpo crudo, leído una sola vez y con tope (sin <c>EnableBuffering</c>): la firma es sobre los bytes
/// exactos y nada pasa por el binder de JSON. Copia de <c>WebhookBodyReader</c> de Messaging, que este módulo no ve.
/// </summary>
internal static class ShopifyWebhookBodyReader
{
    private const int ChunkSize = 16 * 1024;

    public static async Task<ShopifyWebhookBodyReadResult> ReadAsync(
        Stream body, long? declaredLength, int maxBytes, CancellationToken cancellationToken)
    {
        if (declaredLength is { } declared && declared > maxBytes)
        {
            return new(null, StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return new(null, StatusCodes.Status413PayloadTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception)
        {
            // Lo que Kestrel corta al leer (413 en chunked, 400 si el cuerpo es más corto que su Content-Length): un request malo,
            // no una falla nuestra; sin esto el ApiExceptionHandler lo volvería un 500.
            return new(null, exception.StatusCode);
        }

        return new(buffer.ToArray(), StatusCodes.Status200OK);
    }
}
