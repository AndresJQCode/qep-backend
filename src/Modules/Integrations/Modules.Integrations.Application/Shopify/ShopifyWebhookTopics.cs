namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// Spec 2026-10-15 §5.1 y D-S19: los topics que QEP registra en cada tienda, en la forma REST (<c>orders/create</c>), que
/// es la que traen los webhooks en <c>X-Shopify-Topic</c>. El gateway los traduce al enum de GraphQL
/// (<c>ORDERS_CREATE</c>) al crearlos; <see cref="ToGraphQl"/> es esa traducción, para comparar con lo que lista Shopify.
/// Los registran el enlace y el probador, los dos con <see cref="ShopifyWebhookRegistration"/>.
/// </summary>
public static class ShopifyWebhookTopics
{
    /// <summary>Spec 2026-11-17 §6: los de productos, que sólo se piden con <c>read_products</c> (P10).</summary>
    public static readonly IReadOnlyList<string> ProductTopics = ["products/create", "products/update", "products/delete"];

    public static readonly IReadOnlyList<string> Registered =
        ["orders/create", "orders/updated", "orders/paid", "orders/cancelled", "app/uninstalled", .. ProductTopics];

    /// <summary>Spec 2026-11-17 §5.1: <c>write_products</c> cuenta como <c>read_products</c>, la misma regla que el canje. Copia de
    /// <c>ShopifyScopes.CanReadProducts</c> del módulo Shopify (que este módulo no ve), amarradas por
    /// <c>ShopifyProductScopeParityTests</c>: se cambian juntas.</summary>
    public static bool CanReadProducts(IEnumerable<string> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        return granted.Any(scope => scope is "read_products" or "write_products");
    }

    /// <summary>P10: <see cref="Registered"/> sin los de productos si la tienda no concedió <c>read_products</c>. Sin eso, Shopify
    /// rechaza la suscripción y la conexión pasaría a <c>NeedsAttention</c> por un topic que no puede pedir.</summary>
    public static IReadOnlyList<string> ForScopes(IEnumerable<string> granted) =>
        CanReadProducts(granted) ? Registered : [.. Registered.Except(ProductTopics, StringComparer.Ordinal)];

    /// <summary>El campo <c>scopes</c> de la conexión o la instalación (separados por coma) como lista; vacío, ninguno.</summary>
    public static IReadOnlyCollection<string> ParseScopes(string? scopes) =>
        string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary><c>orders/create</c> → <c>ORDERS_CREATE</c>. Un topic que ya viene en la forma de GraphQL queda igual.</summary>
    public static string ToGraphQl(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        return topic.Replace('/', '_').ToUpperInvariant();
    }
}

/// <summary>
/// El resultado de asegurar los webhooks de una tienda. <see cref="SubscriptionIds"/> son los ids, en el orden de
/// <see cref="ShopifyWebhookTopics.Registered"/>, de los topics que quedaron registrados (los que ya estaban y los que se
/// crearon). <see cref="Failure"/> es la clase de la primera falla; con ella, la lista puede quedar incompleta.
/// </summary>
public sealed record ShopifyWebhookRegistrationResult(IReadOnlyList<string> SubscriptionIds, ShopifyGatewayFailure? Failure)
{
    public bool Succeeded => Failure is null;

    /// <summary>Como los guarda la conexión (<c>webhookSubscriptionIds</c>): separados por coma.</summary>
    public string JoinedIds => string.Join(',', SubscriptionIds);
}

/// <summary>
/// Spec 2026-10-15 §4.3 paso 6 y §4.1: lee las suscripciones de la app en la tienda y crea, con formato JSON, cada topic
/// de <see cref="ShopifyWebhookTopics.Registered"/> que falte. Una suscripción cuenta como nuestra si el topic coincide
/// <b>y</b> apunta a <paramref name="callback"/>: la del mismo topic hacia otra URL (una URL vieja de QEP) no recibe nada
/// aquí. Nunca lanza por Shopify: la falla vuelve en el resultado y quien llama decide (el enlace deja
/// <c>NeedsAttention</c>; el probador, su resultado).
/// </summary>
public static class ShopifyWebhookRegistration
{
    /// <param name="grantedScopes">Los scopes concedidos a la app en la tienda: deciden si se piden los <c>products/*</c> (P10).</param>
    public static async Task<ShopifyWebhookRegistrationResult> EnsureAsync(
        IShopifyAdminGateway gateway,
        string shop,
        string token,
        Uri callback,
        IReadOnlyCollection<string> grantedScopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grantedScopes);
        IReadOnlyList<ShopifyWebhookSubscription> existing;
        try
        {
            existing = await gateway.ListWebhooksAsync(shop, token, cancellationToken);
        }
        catch (ShopifyGatewayException exception)
        {
            return new ShopifyWebhookRegistrationResult([], exception.Kind);
        }

        var ids = new List<string>();
        foreach (var topic in ShopifyWebhookTopics.ForScopes(grantedScopes))
        {
            var graphQlTopic = ShopifyWebhookTopics.ToGraphQl(topic);
            var mine = existing.FirstOrDefault(subscription =>
                string.Equals(ShopifyWebhookTopics.ToGraphQl(subscription.Topic), graphQlTopic, StringComparison.Ordinal)
                && subscription.CallbackUrl == callback);
            if (mine is not null)
            {
                ids.Add(mine.Id);
                continue;
            }

            try
            {
                ids.Add((await gateway.CreateWebhookAsync(shop, token, topic, callback, cancellationToken)).Id);
            }
            catch (ShopifyGatewayException exception)
            {
                return new ShopifyWebhookRegistrationResult(ids, exception.Kind);
            }
        }

        return new ShopifyWebhookRegistrationResult(ids, null);
    }
}
