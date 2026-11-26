using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure.Admin;

/// <summary>
/// Spec 2026-11-14 §5 y plan D14: <c>orderUpdate</c> por la Admin API GraphQL, sobre el mismo cliente <c>shopify.admin</c> que la
/// conciliación (sin redirecciones, 10 s, sin los loggers de headers). Manda sólo los campos que vienen, sin alias ni fragmentos.
/// <list type="bullet">
/// <item>2xx sin <c>errors</c> ni <c>userErrors</c> y con <c>order</c> → <c>Updated</c>.</item>
/// <item>429, <c>THROTTLED</c>, 5xx, red, timeout, 401 y 402 → <c>Retry</c> (con <c>Retry-After</c> si llega).</item>
/// <item>403 o <c>ACCESS_DENIED</c> (falta <c>write_orders</c>, V-13) → <c>access_denied</c>; <c>userErrors</c> → <c>user_error</c>;
/// <c>order</c> nulo → <c>order_not_found</c>; otro 4xx o error → <c>rejected</c>.</item>
/// </list>
/// Nunca registra el token, el cuerpo ni el mensaje de un <c>userError</c> (puede traer datos del comprador): del error, sólo el
/// código y los nombres de los campos.
/// </summary>
internal sealed partial class ShopifyOrderUpdateClient(
    IHttpClientFactory httpClientFactory,
    IShopifyApiVersion apiVersion,
    ILogger<ShopifyOrderUpdateClient> logger) : IShopifyOrderUpdater
{
    internal const string Mutation =
        "mutation OrderUpdate($input: OrderInput!) { orderUpdate(input: $input) { order { id updatedAt } userErrors { field message } } }";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify orderUpdate of order {ShopifyOrderId} not applied: {Kind} {Code} (HTTP {StatusCode}, {Detail})")]
    private static partial void LogNotApplied(
        ILogger logger, long shopifyOrderId, ShopifyOrderUpdateKind kind, string code, int statusCode, string detail);

    public async Task<ShopifyOrderUpdateResult> UpdateAsync(
        ShopifyConnectionRef connection, ShopifyOrderUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            throw new InvalidOperationException("The Shopify connection has no access token.");
        }

        var body = new JsonObject { ["query"] = Mutation, ["variables"] = new JsonObject { ["input"] = Input(update) } }.ToJsonString();
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
            using var response = await httpClientFactory.CreateClient(ShopifyGraphQlClient.HttpClientName).SendAsync(request, cancellationToken);
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
            return Retry(update, 0, exception.GetType().Name);
        }

        switch (status)
        {
            case HttpStatusCode.TooManyRequests:
                return Retry(update, (int)status, "http_429", retryAfter);
            case HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or >= HttpStatusCode.InternalServerError:
                return Retry(update, (int)status, "http");
            case HttpStatusCode.Forbidden:
                return Fail(update, ShopifyPushFailureCodes.AccessDenied, (int)status, "http");
            case < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices:
                return Fail(update, ShopifyPushFailureCodes.Rejected, (int)status, "http");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return Retry(update, (int)status, "invalid_json");
        }

        if (root?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var code = errors[0]?["extensions"]?["code"] is JsonValue value && value.TryGetValue(out string? text) ? text : "unknown";
            return code switch
            {
                "THROTTLED" => Retry(update, (int)status, code),
                "ACCESS_DENIED" => Fail(update, ShopifyPushFailureCodes.AccessDenied, (int)status, code),
                _ => Fail(update, ShopifyPushFailureCodes.Rejected, (int)status, code),
            };
        }

        var result = root?["data"]?["orderUpdate"];
        if (result?["userErrors"] is JsonArray { Count: > 0 } userErrors)
        {
            // Sólo los campos: el mensaje puede traer el nombre o la dirección del comprador.
            var fields = string.Join(
                ';',
                userErrors.Select(error => error?["field"] is JsonArray path
                    ? string.Join('.', path.Select(part => part?.ToString()))
                    : "?"));
            return Fail(update, ShopifyPushFailureCodes.UserError, (int)status, $"fields={fields}");
        }

        return result?["order"] is JsonObject
            ? new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Updated, null, null)
            : Fail(update, ShopifyPushFailureCodes.OrderNotFound, (int)status, "order_null");
    }

    // Sólo lo que viene: un campo que no viaja, Shopify lo conserva.
    private static JsonObject Input(ShopifyOrderUpdate update)
    {
        var input = new JsonObject { ["id"] = $"gid://shopify/Order/{update.ShopifyOrderId.ToString(CultureInfo.InvariantCulture)}" };
        if (update.ShippingAddress is { } address)
        {
            var shipping = new JsonObject
            {
                ["firstName"] = address.FirstName,
                ["lastName"] = address.LastName,
                ["address1"] = address.Address1,
                ["address2"] = address.Address2,
                ["city"] = address.City,
                ["countryCode"] = address.CountryCode,
                ["phone"] = address.Phone,
            };
            if (address.ProvinceCode is { } province)
            {
                shipping["provinceCode"] = province;
            }

            if (address.Company is { } company)
            {
                shipping["company"] = company;
            }

            if (address.Zip is { } zip)
            {
                shipping["zip"] = zip;
            }

            input["shippingAddress"] = shipping;
        }

        if (update.SetNote)
        {
            input["note"] = update.Note;
        }

        if (update.Attributes is { } attributes)
        {
            input["customAttributes"] = new JsonArray(
                [.. attributes.Select(pair => (JsonNode)new JsonObject { ["key"] = pair.Key, ["value"] = pair.Value })]);
        }

        return input;
    }

    private ShopifyOrderUpdateResult Retry(ShopifyOrderUpdate update, int statusCode, string detail, TimeSpan? retryAfter = null)
    {
        LogNotApplied(logger, update.ShopifyOrderId, ShopifyOrderUpdateKind.Retry, "retry", statusCode, detail);
        return new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Retry, null, retryAfter);
    }

    private ShopifyOrderUpdateResult Fail(ShopifyOrderUpdate update, string code, int statusCode, string detail)
    {
        LogNotApplied(logger, update.ShopifyOrderId, ShopifyOrderUpdateKind.Failed, code, statusCode, detail);
        return new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Failed, code, null);
    }

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
