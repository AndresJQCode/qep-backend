using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Parsing;

namespace Modules.Shopify.Infrastructure.Admin;

/// <summary>
/// Spec 2026-11-17 §5.2: el catálogo de una tienda por la Admin API GraphQL, sobre el mismo <see cref="ShopifyGraphQlTransport"/>
/// que la conciliación (sin redirecciones, el balde de costo, la clasificación). Scoped: el balde es de una corrida.
/// <para><b>El costo.</b> Una página es <c>products(first: 10)</c> con <c>variants(first: 50)</c> anidadas (≈ 10 × 50 = 500 puntos más
/// los productos, bajo el tope de 1000). Las variantes que no caben se piden aparte con <see cref="ProductVariantsQuery"/> y se unen
/// antes de parsear, con la misma guarda de cursor que la conciliación: uno que se repite es <c>Unavailable</c>.</para>
/// <para><b>Las fallas.</b> A diferencia del cliente de pedidos, 403 y <c>ACCESS_DENIED</c> salen como
/// <see cref="ShopifyProductsSourceFailure.AccessDenied"/>: sin <c>read_products</c> la importación termina <c>scope_missing</c>.
/// Nunca registra ni pone en una excepción el token, el cuerpo de la respuesta ni el mensaje de una excepción de red.</para>
/// </summary>
internal sealed partial class ShopifyProductsClient(
    IHttpClientFactory httpClientFactory,
    IShopifyApiVersion apiVersion,
    TimeProvider time,
    ILogger<ShopifyProductsClient> logger) : IShopifyProductsSource
{
    /// <summary>Lo que se importa: activos y borradores (<c>ARCHIVED</c> no se trae, §5.3).</summary>
    internal const string ProductsFilter = "status:active OR status:draft";

    private const string PageInfo = "pageInfo { hasNextPage endCursor }";

    private const string VariantFields = "id sku barcode price taxable selectedOptions { name value }";

    // Sin fragmentos ni alias a propósito, como la consulta de pedidos. Cada campo que lee GraphQlProductParser está acá
    // (ProductParsersTests lo amarra).
    internal const string ProductFields =
        "id title status updatedAt options { name optionValues { name } } "
        + "featuredMedia { id preview { image { url(transform: { preferredContentType: JPG, maxWidth: 2048, maxHeight: 2048 }) } } } "
        + "variants(first: 50) { " + PageInfo + " nodes { " + VariantFields + " } }";

    internal const string ShopQuery =
        "query Shop($query: String!) { shop { currencyCode taxesIncluded } productsCount(query: $query, limit: null) { count } }";

    internal const string ProductsQuery =
        "query Products($after: String, $query: String!) { products(first: 10, after: $after, query: $query, sortKey: ID) { "
        + PageInfo + " nodes { " + ProductFields + " } } }";

    internal const string ProductQuery = "query Product($id: ID!) { product(id: $id) { " + ProductFields + " } }";

    internal const string ProductVariantsQuery =
        "query ProductVariants($id: ID!, $after: String) { product(id: $id) { variants(first: 50, after: $after) { "
        + PageInfo + " nodes { " + VariantFields + " } } } }";

    internal const string VariantKeysQuery =
        "query VariantKeys($after: String) { productVariants(first: 250, after: $after, query: \"product_status:active OR product_status:draft\") { "
        + PageInfo + " nodes { id sku product { title } } } }";

    private readonly ShopifyGraphQlTransport transport = new(httpClientFactory, apiVersion, time);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify {Operation} failed: {Failure} (HTTP {StatusCode}, {Detail})")]
    private static partial void LogFailed(ILogger logger, string operation, ShopifyProductsSourceFailure failure, int statusCode, string detail);

    public async Task<ShopifyShopInfo> ReadShopAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        var data = await SendAsync("shop", connection, ShopQuery, new JsonObject { ["query"] = ProductsFilter }, cancellationToken);
        var currency = Text(data?["shop"]?["currencyCode"], "shop", "currency") ?? throw Unusable("shop", "no_currency");
        var taxesIncluded = Flag(data?["shop"]?["taxesIncluded"], "shop", "taxes_included");
        var count = data?["productsCount"]?["count"] is JsonValue value && value.TryGetValue(out int number) && number >= 0
            ? number
            : throw Unusable("shop", "no_products_count");
        return new ShopifyShopInfo(currency, taxesIncluded, count);
    }

    public async Task<ShopifyProductsPage> FetchProductsAsync(
        ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken)
    {
        var data = await SendAsync(
            "products", connection, ProductsQuery, new JsonObject { ["after"] = cursor, ["query"] = ProductsFilter }, cancellationToken);
        var products = data?["products"] as JsonObject ?? throw Unusable("products", "no_products");

        var snapshots = new List<ShopifyProductSnapshot>();
        foreach (var node in products["nodes"] as JsonArray ?? [])
        {
            if (node is not JsonObject product)
            {
                throw Unusable("products", "node_not_an_object");
            }

            if (await AppendVariantPagesAsync(connection, product, cancellationToken))
            {
                snapshots.Add(Parse(product));
            }
        }

        return new ShopifyProductsPage(snapshots, NextCursor(products, "products"));
    }

    public async Task<ShopifyProductSnapshot?> ReadProductAsync(
        ShopifyConnectionRef connection, long productId, CancellationToken cancellationToken)
    {
        var gid = ProductGid(productId);
        var data = await SendAsync("product", connection, ProductQuery, new JsonObject { ["id"] = gid }, cancellationToken);
        if (data?["product"] is not JsonObject product || !await AppendVariantPagesAsync(connection, product, cancellationToken))
        {
            return null;
        }

        return Parse(product);
    }

    public async Task<ShopifyVariantKeysPage> ListVariantKeysAsync(
        ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken)
    {
        var data = await SendAsync("variant_keys", connection, VariantKeysQuery, new JsonObject { ["after"] = cursor }, cancellationToken);
        var variants = data?["productVariants"] as JsonObject ?? throw Unusable("variant_keys", "no_variants");

        var keys = new List<ShopifyVariantKey>();
        foreach (var node in variants["nodes"] as JsonArray ?? [])
        {
            var gid = Text(node?["id"], "variant_keys", "id") ?? throw Unusable("variant_keys", "node_without_id");
            var id = IdOf(gid, "ProductVariant") ?? throw Unusable("variant_keys", "id_not_numeric");
            var sku = Text(node?["sku"], "variant_keys", "sku");
            keys.Add(new ShopifyVariantKey(
                id,
                string.IsNullOrWhiteSpace(sku) ? null : sku,
                Text(node?["product"]?["title"], "variant_keys", "title") ?? string.Empty));
        }

        return new ShopifyVariantKeysPage(keys, NextCursor(variants, "variant_keys"));
    }

    /// <summary>Las páginas siguientes de <c>variants</c> del producto, sin tope; el cursor tiene que avanzar. <c>false</c> si el
    /// producto desapareció a mitad de camino.</summary>
    private async Task<bool> AppendVariantPagesAsync(ShopifyConnectionRef connection, JsonObject product, CancellationToken cancellationToken)
    {
        var gid = Text(product["id"], "product", "id") ?? throw Unusable("product", "node_without_id");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (product["variants"] is JsonObject current && Flag(current["pageInfo"]?["hasNextPage"], "variants", "has_next_page"))
        {
            var cursor = Text(current["pageInfo"]?["endCursor"], "variants", "end_cursor");
            if (cursor is null || !seen.Add(cursor))
            {
                throw Unusable("variants", "cursor_not_advancing");
            }

            var data = await SendAsync(
                "variants", connection, ProductVariantsQuery, new JsonObject { ["id"] = gid, ["after"] = cursor }, cancellationToken);
            if (data?["product"]?["variants"] is not JsonObject next)
            {
                return false;
            }

            if (current["nodes"] is not JsonArray nodes)
            {
                current["nodes"] = nodes = [];
            }

            foreach (var node in next["nodes"] as JsonArray ?? [])
            {
                nodes.Add(node?.DeepClone());
            }

            current["pageInfo"] = next["pageInfo"]?.DeepClone();
        }

        return true;
    }

    private ShopifyProductSnapshot Parse(JsonObject product)
    {
        try
        {
            return GraphQlProductParser.Parse(JsonSerializer.SerializeToElement(product));
        }
        catch (ShopifyPayloadException exception)
        {
            // Una respuesta de Shopify que no se puede leer: no es culpa del producto ni de la tienda, y reintentar da lo mismo. Sale
            // como Unavailable con el campo (nunca el valor).
            throw Unusable("product", exception.Field.Length == 0 ? "invalid_payload" : $"invalid_payload:{exception.Field}");
        }
    }

    private string? NextCursor(JsonObject connection, string operation)
    {
        var hasNextPage = Flag(connection["pageInfo"]?["hasNextPage"], operation, "has_next_page");
        var endCursor = Text(connection["pageInfo"]?["endCursor"], operation, "end_cursor");
        if (hasNextPage && endCursor is null)
        {
            throw Unusable(operation, "next_page_without_cursor");
        }

        return hasNextPage ? endCursor : null;
    }

    private async Task<JsonNode?> SendAsync(
        string operation, ShopifyConnectionRef connection, string query, JsonObject variables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            throw new InvalidOperationException("The Shopify connection has no access token.");
        }

        try
        {
            return await transport.SendAsync(operation, connection, query, variables, cancellationToken);
        }
        catch (ShopifyGraphQlTransportException exception)
        {
            var failure = exception.Failure switch
            {
                ShopifyGraphQlFailure.Throttled => ShopifyProductsSourceFailure.Throttled,
                ShopifyGraphQlFailure.Rejected => ShopifyProductsSourceFailure.Rejected,
                ShopifyGraphQlFailure.AccessDenied => ShopifyProductsSourceFailure.AccessDenied,
                _ => ShopifyProductsSourceFailure.Unavailable,
            };
            throw Fail(exception.Operation, failure, exception.StatusCode, exception.Detail, exception.RetryAfter);
        }
    }

    private string? Text(JsonNode? node, string operation, string detail) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out string? text) => text,
        _ => throw Unusable(operation, $"{detail}_not_a_string"),
    };

    private bool Flag(JsonNode? node, string operation, string detail) => node switch
    {
        null => false,
        JsonValue value when value.TryGetValue(out bool flag) => flag,
        _ => throw Unusable(operation, $"{detail}_not_a_boolean"),
    };

    private static string ProductGid(long productId) =>
        string.Create(CultureInfo.InvariantCulture, $"gid://shopify/Product/{productId}");

    private static long? IdOf(string gid, string type)
    {
        var prefix = $"gid://shopify/{type}/";
        return gid.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(gid[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
    }

    private ShopifyProductsSourceException Fail(
        string operation, ShopifyProductsSourceFailure failure, int statusCode, string detail, TimeSpan? retryAfter = null)
    {
        LogFailed(logger, operation, failure, statusCode, detail);
        return new ShopifyProductsSourceException(failure, retryAfter, $"{operation}, HTTP {statusCode}, {detail}");
    }

    private ShopifyProductsSourceException Unusable(string operation, string detail)
    {
        var unusable = ShopifyGraphQlTransport.Unusable(operation, detail);
        return Fail(operation, ShopifyProductsSourceFailure.Unavailable, unusable.StatusCode, detail);
    }
}
