using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Admin;

/// <summary>Cómo falló una consulta, antes de que cada cliente la traduzca a su contrato.</summary>
internal enum ShopifyGraphQlFailure
{
    /// <summary><c>THROTTLED</c> en el cuerpo o HTTP 429.</summary>
    Throttled,

    /// <summary>401 o 402.</summary>
    Rejected,

    /// <summary>403 o <c>ACCESS_DENIED</c>: de alcance. El cliente de pedidos lo trata como <see cref="Unavailable"/>; el de
    /// productos, como <c>scope_missing</c>.</summary>
    AccessDenied,

    /// <summary>Red, tiempo agotado, 5xx, otro error de GraphQL o una respuesta que no se puede usar.</summary>
    Unavailable,
}

/// <summary>Una falla clasificada del transporte. El mensaje sólo lleva la operación, el status y un detalle sin datos (nunca el
/// token, el cuerpo ni el mensaje de una excepción de red).</summary>
internal sealed class ShopifyGraphQlTransportException(
    ShopifyGraphQlFailure failure, TimeSpan? retryAfter, int statusCode, string operation, string detail)
    : Exception($"Shopify {operation} failed: {failure} (HTTP {statusCode}, {detail}).")
{
    public ShopifyGraphQlFailure Failure { get; } = failure;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public int StatusCode { get; } = statusCode;

    public string Operation { get; } = operation;

    public string Detail { get; } = detail;
}

/// <summary>
/// La Admin API GraphQL de una tienda sobre <c>shopify.admin</c> (sin redirecciones, 10 s, sin los loggers de headers), extraída de
/// <c>ShopifyGraphQlClient</c> para que el cliente de pedidos y el de productos compartan el envío, la clasificación y el balde.
/// <para><b>El balde.</b> Cada respuesta trae <c>extensions.cost.throttleStatus</c>; antes de la consulta siguiente del mismo tipo, si
/// lo disponible no alcanza para lo que costó la anterior, espera lo que tarda en reponerse (con <see cref="TimeProvider"/>). El estado
/// es de la instancia: un cliente scoped lleva el balde de una corrida.</para>
/// <para>Nunca registra ni pone en una excepción el token, el cuerpo de la respuesta ni el mensaje de una excepción de red.</para>
/// </summary>
internal sealed class ShopifyGraphQlTransport(
    IHttpClientFactory httpClientFactory,
    IShopifyApiVersion apiVersion,
    TimeProvider time)
{
    public const string HttpClientName = "shopify.admin";

    private readonly Dictionary<string, double> lastCost = new(StringComparer.Ordinal);

    private double? available;

    private double restoreRate;

    /// <summary>El <c>data</c> de la respuesta; una falla sale como <see cref="ShopifyGraphQlTransportException"/>.</summary>
    public async Task<JsonNode?> SendAsync(
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
            throw new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.Unavailable, null, 0, operation, exception.GetType().Name);
        }

        switch (status)
        {
            case HttpStatusCode.TooManyRequests:
                throw new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.Throttled, retryAfter, (int)status, operation, "http_429");
            // Sólo 401 y 402 son credencial rechazada.
            case HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired:
                throw new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.Rejected, null, (int)status, operation, "http");
            case HttpStatusCode.Forbidden:
                throw new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.AccessDenied, null, (int)status, operation, "http");
            case < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices:
                throw new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.Unavailable, null, (int)status, operation, "http");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            throw new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.Unavailable, null, (int)status, operation, "invalid_json");
        }

        var cost = ReadCost(query, root);
        if (root?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var code = errors[0]?["extensions"]?["code"] is JsonValue value && value.TryGetValue(out string? text) ? text : "unknown";
            throw code switch
            {
                "THROTTLED" => new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.Throttled, cost, (int)status, operation, code),
                "ACCESS_DENIED" => new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.AccessDenied, null, (int)status, operation, code),
                _ => new ShopifyGraphQlTransportException(ShopifyGraphQlFailure.Unavailable, null, (int)status, operation, code),
            };
        }

        return root?["data"];
    }

    /// <summary>Una respuesta 200 que no se puede usar (tipo inesperado, cursor que no avanza).</summary>
    public static ShopifyGraphQlTransportException Unusable(string operation, string detail) =>
        new(ShopifyGraphQlFailure.Unavailable, null, (int)HttpStatusCode.OK, operation, detail);

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
}
