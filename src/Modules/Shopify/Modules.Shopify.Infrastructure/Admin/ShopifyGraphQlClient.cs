using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Parsing;

namespace Modules.Shopify.Infrastructure.Admin;

/// <summary>
/// Spec 2026-10-15 §5.3: la Admin API GraphQL de una tienda para la conciliación, sobre <c>shopify.admin</c> con el patrón de
/// <c>ShopifyAdminGateway</c> de Integrations: sin redirecciones (un 3xx no lleva el token a otro host), 10 s, sin los loggers de
/// headers. La versión de la API es <c>Shopify:App:ApiVersion</c> (<see cref="IShopifyApiVersion"/>, un solo lugar).
/// <para><b>Dos pasos y no uno.</b> Shopify rechaza toda consulta cuyo costo pedido pase de 1000 puntos, y el costo de una conexión
/// es su <c>first</c> por el de cada hijo: <c>orders(first: 50)</c> con las líneas anidadas cuesta varios miles. Por eso la página
/// trae sólo ids (<see cref="OrdersQuery"/>, ≈ 52 puntos) y cada pedido se pide entero con <see cref="OrderQuery"/> (≈ 400). Las
/// líneas de un pedido que no caben en una página (<c>lineItems(first: 50)</c>, <c>shippingLines(first: 10)</c>) se piden aparte y
/// se unen antes de parsear: un pedido truncado entraría con menos productos y sólo avisaría <c>total_difference</c>.</para>
/// <para><b>Los campos.</b> <see cref="OrderQuery"/> pide exactamente lo que lee <see cref="GraphQlOrderParser"/>: uno que no se pida
/// llega como 0, <c>false</c> o <c>null</c> sin error. <c>ShopifyGraphQlQueryContractTests</c> lo amarra. El contacto del cliente va
/// por <c>defaultEmailAddress</c>/<c>defaultPhoneNumber</c>: <c>Customer.email</c> y <c>Customer.phone</c> están deprecados en la
/// 2026-10. <c>shippingLines(includeRemovals: true)</c> trae las líneas de envío retiradas, que el importador descarta.</para>
/// <para><b>El balde.</b> Cada respuesta trae <c>extensions.cost.throttleStatus</c>; antes de la consulta siguiente del mismo tipo, si
/// lo disponible no alcanza para lo que costó la anterior, espera lo que tarda en reponerse (con <see cref="TimeProvider"/>) en vez
/// de gastarla en un <c>THROTTLED</c>. El <c>THROTTLED</c> que igual llegue sale con la espera calculada y lo maneja
/// <see cref="ReconcileStore"/>.</para>
/// <para>Nunca registra ni pone en una excepción el token, el cuerpo de la respuesta ni el mensaje de una excepción de red.</para>
/// </summary>
internal sealed partial class ShopifyGraphQlClient(
    IHttpClientFactory httpClientFactory,
    IShopifyApiVersion apiVersion,
    TimeProvider time,
    ILogger<ShopifyGraphQlClient> logger) : IShopifyOrdersSource
{
    public const string HttpClientName = "shopify.admin";

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Sin fragmentos ni alias a propósito: la prueba de contrato lee la consulta con un analizador mínimo.
    private const string LineFields =
        "sku quantity originalUnitPriceSet { shopMoney { amount } } discountAllocations { allocatedAmountSet { shopMoney { amount } } }";

    private const string ShippingLineFields =
        "code title discountedPriceSet { shopMoney { amount } } taxLines { priceSet { shopMoney { amount } } } isRemoved";

    private const string PageInfo = "pageInfo { hasNextPage endCursor }";

    internal const string OrdersQuery =
        "query Orders($after: String, $query: String!) { orders(first: 50, after: $after, sortKey: UPDATED_AT, query: $query) { "
        + PageInfo + " nodes { id } } }";

    internal const string OrderQuery =
        "query Order($id: ID!) { order(id: $id) { "
        + "id name email phone createdAt updatedAt cancelledAt cancelReason displayFinancialStatus currencyCode "
        + "totalPriceSet { shopMoney { amount } } taxesIncluded test note paymentGatewayNames "
        + "customer { id firstName lastName defaultEmailAddress { emailAddress } defaultPhoneNumber { phoneNumber } } "
        + "shippingAddress { name address1 address2 city province provinceCode countryCodeV2 phone } "
        + "customAttributes { key value } "
        + "lineItems(first: 50) { " + PageInfo + " nodes { " + LineFields + " } } "
        + "shippingLines(first: 10, includeRemovals: true) { " + PageInfo + " nodes { " + ShippingLineFields + " } } "
        + "} }";

    internal const string OrderLinesQuery =
        "query OrderLines($id: ID!, $after: String) { order(id: $id) { lineItems(first: 50, after: $after) { "
        + PageInfo + " nodes { " + LineFields + " } } } }";

    internal const string OrderShippingLinesQuery =
        "query OrderShippingLines($id: ID!, $after: String) { order(id: $id) { shippingLines(first: 10, after: $after, includeRemovals: true) { "
        + PageInfo + " nodes { " + ShippingLineFields + " } } } }";

    private readonly Dictionary<string, double> lastCost = new(StringComparer.Ordinal);

    private double? available;

    private double restoreRate;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify {Operation} failed: {Failure} (HTTP {StatusCode}, {Detail})")]
    private static partial void LogFailed(ILogger logger, string operation, ShopifyOrdersSourceFailure failure, int statusCode, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify order {OrderGid} skipped by the reconciliation: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string orderGid, string reason);

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = Timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-shopify");
    }

    public async Task<ShopifyOrdersPage> FetchUpdatedSinceAsync(
        ShopifyConnectionRef connection, DateTimeOffset since, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            throw new InvalidOperationException("The Shopify connection has no access token; only an operational connection is reconciled.");
        }

        var data = await SendAsync("orders", connection, OrdersQuery, new JsonObject
        {
            ["after"] = cursor,
            ["query"] = $"updated_at:>='{since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}'",
        }, cancellationToken);
        var orders = data?["orders"] as JsonObject ?? throw Unusable("orders", "no_orders");

        var snapshots = new List<ShopifyOrderSnapshot>();
        var skipped = new List<ShopifySkippedOrder>();
        foreach (var node in orders["nodes"] as JsonArray ?? [])
        {
            var gid = Text(node?["id"], "orders", "node_id") ?? throw Unusable("orders", "node_without_id");
            switch (await ReadOrderAsync(connection, gid, cancellationToken))
            {
                case { Snapshot: { } snapshot }:
                    snapshots.Add(snapshot);
                    break;
                case { Skipped: { } unreadable }:
                    skipped.Add(unreadable);
                    break;
            }
        }

        var hasNextPage = Flag(orders["pageInfo"]?["hasNextPage"], "orders");
        var endCursor = Text(orders["pageInfo"]?["endCursor"], "orders", "end_cursor");
        if (hasNextPage && endCursor is null)
        {
            throw Unusable("orders", "next_page_without_cursor");
        }

        return new ShopifyOrdersPage(snapshots, hasNextPage ? endCursor : null) { Skipped = skipped };
    }

    private async Task<(ShopifyOrderSnapshot? Snapshot, ShopifySkippedOrder? Skipped)> ReadOrderAsync(
        ShopifyConnectionRef connection, string gid, CancellationToken cancellationToken)
    {
        var data = await SendAsync("order", connection, OrderQuery, new JsonObject { ["id"] = gid }, cancellationToken);
        if (data?["order"] is not JsonObject order
            || !await AppendPagesAsync(connection, gid, order, "lineItems", OrderLinesQuery, cancellationToken)
            || !await AppendPagesAsync(connection, gid, order, "shippingLines", OrderShippingLinesQuery, cancellationToken))
        {
            // Se borró entre la lista y el detalle (o mientras se leían sus líneas): no es un error, no hay nada que anotar.
            LogSkipped(logger, gid, "not_found");
            return (null, null);
        }

        try
        {
            return (GraphQlOrderParser.Parse(JsonSerializer.SerializeToElement(order)), null);
        }
        catch (ShopifyPayloadException exception)
        {
            // El mismo fallo permanente que un webhook ilegible: no frena la página, y vuelve con su id y su motivo
            // para que la conciliación lo anote en el espejo y lo cuente. Sin id numérico no hay fila que anotar, pero se cuenta.
            var reason = exception.Field.Length == 0
                ? $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {exception.Message}"
                : $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {exception.Field}: {exception.Message}";
            LogSkipped(logger, gid, exception.Field.Length == 0 ? "invalid_payload" : $"invalid_payload:{exception.Field}");
            return (null, new ShopifySkippedOrder(OrderIdOf(gid), reason));
        }
    }

    /// <summary>
    /// Las páginas siguientes de <paramref name="field"/>, hasta la última, sin tope, porque un pedido con
    /// líneas de menos entraría con otro total y sólo avisaría <c>total_difference</c>. Lo que protege de un bucle sin fin es que
    /// el cursor tiene que avanzar: uno que se repite, o una página «siguiente» sin cursor, es una respuesta rota (<c>Unavailable</c>,
    /// la corrida no mueve la ventana). <c>false</c> si el pedido desapareció a mitad de camino.
    /// </summary>
    private async Task<bool> AppendPagesAsync(
        ShopifyConnectionRef connection, string gid, JsonObject order, string field, string query, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (order[field] is JsonObject current && Flag(current["pageInfo"]?["hasNextPage"], field))
        {
            var cursor = Text(current["pageInfo"]?["endCursor"], field, "end_cursor");
            if (cursor is null || !seen.Add(cursor))
            {
                throw Unusable(field, "cursor_not_advancing");
            }

            var data = await SendAsync(field, connection, query, new JsonObject { ["id"] = gid, ["after"] = cursor }, cancellationToken);
            if (data?["order"]?[field] is not JsonObject next)
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

    // Un tipo inesperado donde se espera texto o booleano es una respuesta inusable (503 en el sync), no un
    // InvalidOperationException sin clasificar (500).
    private string? Text(JsonNode? node, string operation, string detail) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out string? text) => text,
        _ => throw Unusable(operation, $"{detail}_not_a_string"),
    };

    private bool Flag(JsonNode? node, string operation) => node switch
    {
        null => false,
        JsonValue value when value.TryGetValue(out bool flag) => flag,
        _ => throw Unusable(operation, "has_next_page_not_a_boolean"),
    };

    private static long? OrderIdOf(string gid) =>
        gid.StartsWith("gid://shopify/Order/", StringComparison.Ordinal)
        && long.TryParse(gid["gid://shopify/Order/".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    private async Task<JsonNode?> SendAsync(
        string operation, ShopifyConnectionRef connection, string query, JsonObject variables, CancellationToken cancellationToken)
    {
        await PaceAsync(query, cancellationToken);
        var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"https://{Host(connection.ShopDomain)}/admin/api/{apiVersion.Value}/graphql.json", UriKind.Absolute))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", connection.AccessToken);

        string content;
        HttpStatusCode status;
        TimeSpan? retryAfter;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            status = response.StatusCode;
            retryAfter = RetryAfterHeader(response);
            content = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // Ni el mensaje ni la excepción viajan: pueden traer la URL o los headers.
            throw Fail(operation, ShopifyOrdersSourceFailure.Unavailable, 0, exception.GetType().Name);
        }

        switch (status)
        {
            case HttpStatusCode.TooManyRequests:
                throw Fail(operation, ShopifyOrdersSourceFailure.Throttled, (int)status, "http_429", retryAfter);
            // §5.3: sólo 401 y 402 son credencial rechazada. Un 403 cae abajo, a Unavailable.
            case HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired:
                throw Fail(operation, ShopifyOrdersSourceFailure.Rejected, (int)status, "http");
            case < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices:
                throw Fail(operation, ShopifyOrdersSourceFailure.Unavailable, (int)status, "http");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            throw Fail(operation, ShopifyOrdersSourceFailure.Unavailable, (int)status, "invalid_json");
        }

        var cost = ReadCost(query, root);
        if (root?["errors"] is JsonArray { Count: > 0 } errors)
        {
            // ACCESS_DENIED es de alcance (p. ej. sin la aprobación de datos protegidos, V-3), no de credencial: Unavailable con su
            // código en el log, sin reportar la conexión, que apagaría también el webhook.
            var code = errors[0]?["extensions"]?["code"] is JsonValue value && value.TryGetValue(out string? text) ? text : "unknown";
            throw code switch
            {
                "THROTTLED" => Fail(operation, ShopifyOrdersSourceFailure.Throttled, (int)status, code, cost),
                _ => Fail(operation, ShopifyOrdersSourceFailure.Unavailable, (int)status, code),
            };
        }

        return root?["data"];
    }

    // Lee el balde de la respuesta y devuelve lo que falta para la consulta pedida, si falta algo.
    private TimeSpan? ReadCost(string query, JsonNode? root)
    {
        var cost = root?["extensions"]?["cost"];
        if (Number(cost?["throttleStatus"]?["currentlyAvailable"]) is not { } current
            || Number(cost?["throttleStatus"]?["restoreRate"]) is not { } rate || rate <= 0)
        {
            return null;
        }

        available = current;
        restoreRate = rate;
        if (Number(cost?["requestedQueryCost"]) is not { } requested)
        {
            return null;
        }

        lastCost[query] = requested;
        return requested > current ? TimeSpan.FromSeconds((requested - current) / rate) : null;
    }

    private async Task PaceAsync(string query, CancellationToken cancellationToken)
    {
        if (available is { } current && restoreRate > 0 && lastCost.TryGetValue(query, out var needed) && needed > current)
        {
            await Task.Delay(TimeSpan.FromSeconds((needed - current) / restoreRate), time, cancellationToken);
            available = needed;
        }
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static TimeSpan? RetryAfterHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values)
        && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    // El dominio arma el host de la URL que lleva el token: sólo una tienda *.myshopify.com ya normalizada.
    private static string Host(string shopDomain) =>
        shopDomain.EndsWith(".myshopify.com", StringComparison.Ordinal)
        && shopDomain.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')
            ? shopDomain
            : throw new InvalidOperationException("The connection shop domain is not a normalized *.myshopify.com domain.");

    private ShopifyOrdersSourceException Fail(
        string operation, ShopifyOrdersSourceFailure failure, int statusCode, string detail, TimeSpan? retryAfter = null)
    {
        LogFailed(logger, operation, failure, statusCode, detail);
        return new ShopifyOrdersSourceException(failure, retryAfter, $"{operation}, HTTP {statusCode}, {detail}");
    }

    private ShopifyOrdersSourceException Unusable(string operation, string detail) =>
        Fail(operation, ShopifyOrdersSourceFailure.Unavailable, (int)HttpStatusCode.OK, detail);
}
