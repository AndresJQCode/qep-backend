using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Admin;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-14 §5 y plan D14: <c>orderUpdate</c> contra un <see cref="HttpMessageHandler"/> falso. El cuerpo lleva sólo los campos
/// que vienen, y la respuesta se clasifica en actualizado, reintentar o fallido con su código.
/// </summary>
public sealed class ShopifyOrderUpdateClientTests
{
    private const string Token = "shpat_super_secret_token";

    private static readonly ShopifyConnectionRef Connection =
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "954889", "mi-tienda.myshopify.com", true, Token);

    private static readonly ShopifyOrderUpdate AttributesOnly =
        new(5550000000001, null, false, null, new Dictionary<string, string> { ["documento"] = "1036000111" });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Updated = """{"data":{"orderUpdate":{"order":{"id":"gid://shopify/Order/5550000000001","updatedAt":"2026-11-14T15:00:00Z"},"userErrors":[]}}}""";

    private static (ShopifyOrderUpdateClient Client, FakeShopifyHandler Handler) Bed(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var handler = new FakeShopifyHandler(respond);
        return (new ShopifyOrderUpdateClient(new FakeHttpClientFactory(handler), new FixedApiVersion("2026-10"),
            NullLogger<ShopifyOrderUpdateClient>.Instance), handler);
    }

    [Fact]
    public async Task TheMutationCarriesOnlyTheGivenFields()
    {
        var (client, handler) = Bed((_, _) => Json(HttpStatusCode.OK, Updated));

        var result = await client.UpdateAsync(Connection, AttributesOnly, Ct);

        Assert.Equal(ShopifyOrderUpdateKind.Updated, result.Kind);
        var (uri, token, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://mi-tienda.myshopify.com/admin/api/2026-10/graphql.json", uri.ToString());
        Assert.Equal(Token, token);
        var input = JsonNode.Parse(body)!["variables"]!["input"]!.AsObject();
        Assert.Equal("gid://shopify/Order/5550000000001", input["id"]!.GetValue<string>());
        Assert.False(input.ContainsKey("note"));
        Assert.False(input.ContainsKey("shippingAddress"));
        Assert.Equal("documento", input["customAttributes"]![0]!["key"]!.GetValue<string>());
        Assert.Contains("orderUpdate(input: $input)", JsonNode.Parse(body)!["query"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("050021")]
    [InlineData(null)]
    public async Task TheZipTravelsOnlyWhenThereIsOne(string? zip)
    {
        var (client, handler) = Bed((_, _) => Json(HttpStatusCode.OK, Updated));
        var address = new ShopifyAddressUpdate(
            "Laura Gómez", "", "Cra 5 # 10-20", "", "ENVIGADO", "ANT", "CO", "3001112233", null, zip);

        await client.UpdateAsync(Connection, new ShopifyOrderUpdate(5550000000001, address, false, null, null), Ct);

        var sent = JsonNode.Parse(Assert.Single(handler.Requests).Body)!["variables"]!["input"]!["shippingAddress"]!.AsObject();
        Assert.Equal(zip is not null, sent.ContainsKey("zip"));
        Assert.Equal(zip, sent["zip"]?.GetValue<string>());
    }

    [Fact]
    public async Task TheAddressAndAClearedNoteTravel()
    {
        var (client, handler) = Bed((_, _) => Json(HttpStatusCode.OK, Updated));
        var update = new ShopifyOrderUpdate(
            5550000000001,
            new ShopifyAddressUpdate("Laura Gómez", "", "Cra 5 # 10-20", "", "ENVIGADO", "ANT", "CO", "3001112233", "1036000111"),
            true,
            null,
            null);

        await client.UpdateAsync(Connection, update, Ct);

        var input = JsonNode.Parse(Assert.Single(handler.Requests).Body)!["variables"]!["input"]!.AsObject();
        Assert.True(input.ContainsKey("note"));
        Assert.Null(input["note"]);
        var address = input["shippingAddress"]!;
        Assert.Equal(
            ("Laura Gómez", "", "ENVIGADO", "ANT", "CO", "1036000111"),
            (address["firstName"]!.GetValue<string>(), address["lastName"]!.GetValue<string>(), address["city"]!.GetValue<string>(),
                address["provinceCode"]!.GetValue<string>(), address["countryCode"]!.GetValue<string>(), address["company"]!.GetValue<string>()));
        Assert.False(input.ContainsKey("customAttributes"));
    }

    [Fact]
    public async Task UserErrorsAreUserError()
    {
        var (client, _) = Bed((_, _) => Json(
            HttpStatusCode.OK,
            """{"data":{"orderUpdate":{"order":null,"userErrors":[{"field":["shippingAddress","city"],"message":"City is invalid for Laura"}]}}}"""));

        var result = await client.UpdateAsync(Connection, AttributesOnly, Ct);

        Assert.Equal((ShopifyOrderUpdateKind.Failed, "user_error"), (result.Kind, result.FailureCode));
    }

    [Fact]
    public async Task AMissingOrderIsOrderNotFound()
    {
        var (client, _) = Bed((_, _) => Json(HttpStatusCode.OK, """{"data":{"orderUpdate":{"order":null,"userErrors":[]}}}"""));

        var result = await client.UpdateAsync(Connection, AttributesOnly, Ct);

        Assert.Equal((ShopifyOrderUpdateKind.Failed, "order_not_found"), (result.Kind, result.FailureCode));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(402)]
    public async Task TransientStatusesRetry(int status)
    {
        var (client, _) = Bed((_, _) =>
        {
            var response = Json((HttpStatusCode)status, """{"errors":"nope"}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "2.0");
            return response;
        });

        var result = await client.UpdateAsync(Connection, AttributesOnly, Ct);

        Assert.Equal(ShopifyOrderUpdateKind.Retry, result.Kind);
        Assert.Equal(status == 429 ? (TimeSpan?)TimeSpan.FromSeconds(2) : null, result.RetryAfter);
    }

    [Fact]
    public async Task ThrottledInTheBodyRetries()
    {
        var (client, _) = Bed((_, _) => Json(HttpStatusCode.OK, """{"errors":[{"message":"Throttled","extensions":{"code":"THROTTLED"}}]}"""));

        Assert.Equal(ShopifyOrderUpdateKind.Retry, (await client.UpdateAsync(Connection, AttributesOnly, Ct)).Kind);
    }

    [Fact]
    public async Task ANetworkFailureRetries()
    {
        var (client, _) = Bed((_, _) => throw new HttpRequestException("connection refused"));

        Assert.Equal(ShopifyOrderUpdateKind.Retry, (await client.UpdateAsync(Connection, AttributesOnly, Ct)).Kind);
    }

    [Fact]
    public async Task AccessDeniedIsFailed()
    {
        var (accessDenied, _) = Bed((_, _) => Json(
            HttpStatusCode.OK, """{"errors":[{"message":"Access denied for orderUpdate field.","extensions":{"code":"ACCESS_DENIED"}}]}"""));
        var (forbidden, _) = Bed((_, _) => Json(HttpStatusCode.Forbidden, """{"errors":"Forbidden"}"""));

        var results = new[]
        {
            await accessDenied.UpdateAsync(Connection, AttributesOnly, Ct),
            await forbidden.UpdateAsync(Connection, AttributesOnly, Ct),
        };

        Assert.All(results, result => Assert.Equal((ShopifyOrderUpdateKind.Failed, "access_denied"), (result.Kind, result.FailureCode)));
    }

    [Fact]
    public async Task AnotherClientErrorIsRejected()
    {
        var (client, _) = Bed((_, _) => Json(HttpStatusCode.BadRequest, """{"errors":"bad"}"""));

        var result = await client.UpdateAsync(Connection, AttributesOnly, Ct);

        Assert.Equal((ShopifyOrderUpdateKind.Failed, "rejected"), (result.Kind, result.FailureCode));
    }
}
