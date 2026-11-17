namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 §5.2: el catálogo de una tienda por la Admin API GraphQL (filtro <c>status:active OR status:draft</c>). La
/// implementación vive en Infrastructure (<c>ShopifyProductsClient</c>), con el mismo transporte y el mismo ritmo de costo que la
/// conciliación de pedidos. Una falla sale como <see cref="ShopifyProductsSourceException"/> con su clase.
/// </summary>
public interface IShopifyProductsSource
{
    Task<ShopifyShopInfo> ReadShopAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken);

    /// <summary>Una página de 10 productos ordenada por id; <paramref name="cursor"/> nulo es la primera.</summary>
    Task<ShopifyProductsPage> FetchProductsAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken);

    /// <summary>Un producto con todas sus variantes, o <c>null</c> si ya no existe.</summary>
    Task<ShopifyProductSnapshot?> ReadProductAsync(ShopifyConnectionRef connection, long productId, CancellationToken cancellationToken);

    /// <summary>Una página de 250 variantes (activas o borradores) con su SKU, para la cobertura.</summary>
    Task<ShopifyVariantKeysPage> ListVariantKeysAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken);
}

public enum ShopifyProductsSourceFailure
{
    /// <summary><c>THROTTLED</c> o HTTP 429: se reintenta después de <see cref="ShopifyProductsSourceException.RetryAfter"/>.</summary>
    Throttled,

    /// <summary>401 o 402: la credencial ya no sirve (<c>token_expired</c>).</summary>
    Rejected,

    /// <summary>403 o <c>ACCESS_DENIED</c>: falta el scope (<c>scope_missing</c>). El cliente de pedidos, en cambio, lo trata
    /// como <c>Unavailable</c>.</summary>
    AccessDenied,

    /// <summary>Red, tiempo agotado, 5xx o una respuesta que no se puede usar.</summary>
    Unavailable,
}

/// <summary>Una falla de Shopify clasificada. El mensaje nunca lleva el token, el cuerpo ni el mensaje de una excepción de red.</summary>
public sealed class ShopifyProductsSourceException : Exception
{
    public ShopifyProductsSourceException(ShopifyProductsSourceFailure failure, TimeSpan? retryAfter, string? detail = null)
        : base($"Shopify products source failed: {failure}{(detail is null ? string.Empty : $" ({detail})")}.")
    {
        Failure = failure;
        RetryAfter = retryAfter;
    }

    public ShopifyProductsSourceFailure Failure { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>Plan D1: del cuerpo REST de <c>products/*</c> sólo el id y el <c>updated_at</c>. Un cuerpo ilegible es
/// <see cref="ShopifyPayloadException"/> (fallo permanente).</summary>
public interface IShopifyProductPayloadParser
{
    ShopifyProductWebhookRef ParseRef(string payload);
}
