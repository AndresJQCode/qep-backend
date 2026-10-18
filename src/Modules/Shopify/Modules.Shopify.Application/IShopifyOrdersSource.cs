namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.3: los pedidos de una tienda que cambiaron desde un instante, por la Admin API GraphQL
/// (<c>orders(first: 50, after:, sortKey: UPDATED_AT, query: "updated_at:&gt;='…'")</c>), ya normalizados con el mismo
/// <see cref="ShopifyOrderSnapshot"/> del webhook. La implementación vive en Infrastructure (<c>ShopifyGraphQlClient</c>).
/// <para>Una falla sale como <see cref="ShopifyOrdersSourceException"/> con su clase; qué hacer con ella (esperar, reportar la
/// conexión, dejar la tienda para la próxima corrida) lo decide <see cref="ReconcileStore"/>.</para>
/// </summary>
public interface IShopifyOrdersSource
{
    /// <summary>Una página, de los más viejos a los más nuevos por <c>updated_at</c>. <paramref name="cursor"/> nulo = la
    /// primera. Un pedido que el parser no sabe leer se salta (queda en el log con su id): no tumba la página.</summary>
    Task<ShopifyOrdersPage> FetchUpdatedSinceAsync(
        ShopifyConnectionRef connection, DateTimeOffset since, string? cursor, CancellationToken cancellationToken);
}

/// <summary>Una página de pedidos; <see cref="NextCursor"/> nulo en la última. <see cref="Skipped"/> son los pedidos que Shopify
/// devolvió y el parser no supo leer (fallo permanente, ruling S24): la conciliación los anota en el espejo y los cuenta, para que
/// no desaparezcan.</summary>
public sealed record ShopifyOrdersPage(IReadOnlyList<ShopifyOrderSnapshot> Orders, string? NextCursor)
{
    public IReadOnlyList<ShopifySkippedOrder> Skipped { get; init; } = [];
}

/// <summary>Un pedido que no se pudo leer: su id de Shopify y el motivo con el formato del <c>last_error</c> del webhook
/// (<c>invalid_payload: campo: mensaje</c>).</summary>
public sealed record ShopifySkippedOrder(long OrderId, string Reason);

public enum ShopifyOrdersSourceFailure
{
    /// <summary><c>THROTTLED</c> en el cuerpo o HTTP 429: se reintenta después de <see cref="ShopifyOrdersSourceException.RetryAfter"/>.</summary>
    Throttled,

    /// <summary>401 o 402 (tienda congelada), y sólo esos (§5.3, ruling S24): la credencial ya no sirve; la conexión se reporta.</summary>
    Rejected,

    /// <summary>Red, tiempo agotado, 403, 5xx, <c>ACCESS_DENIED</c> (de alcance: p. ej. sin la aprobación de datos protegidos) o
    /// una respuesta que no se puede usar: se reintenta en la próxima corrida, sin tocar la conexión.</summary>
    Unavailable,
}

/// <summary>Una falla de Shopify clasificada. El mensaje nunca lleva el token, el cuerpo ni el mensaje de la excepción de red
/// (que puede traer la URL o los headers): sólo la operación y el status.</summary>
public sealed class ShopifyOrdersSourceException : Exception
{
    public ShopifyOrdersSourceException(ShopifyOrdersSourceFailure failure, TimeSpan? retryAfter, string? detail = null)
        : base($"Shopify orders source failed: {failure}{(detail is null ? string.Empty : $" ({detail})")}.")
    {
        Failure = failure;
        RetryAfter = retryAfter;
    }

    public ShopifyOrdersSourceFailure Failure { get; }

    /// <summary>Cuánto pide esperar Shopify (<c>Retry-After</c> o lo que falta para reponer el balde); nulo si no lo dijo.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>La versión de la Admin API (<c>Shopify:App:ApiVersion</c>, D-S29) en un solo lugar: el adaptador de Bootstrapper la
/// lee de la misma configuración que usa Integrations para el enlace. La consulta de la conciliación pide los campos de esa
/// versión (<c>defaultEmailAddress</c>, <c>defaultPhoneNumber</c>).</summary>
public interface IShopifyApiVersion
{
    string Value { get; }
}
