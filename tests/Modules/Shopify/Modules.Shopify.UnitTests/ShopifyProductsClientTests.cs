using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Admin;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 §5.2: el cliente de productos sobre el mismo transporte que la conciliación. Las variantes que no caben en
/// <c>variants(first: 50)</c> se piden aparte y se unen antes de parsear; un cursor que se repite es una respuesta rota. A diferencia
/// del cliente de pedidos, <c>ACCESS_DENIED</c> y 403 son su propia falla (<c>scope_missing</c>).
/// </summary>
public sealed class ShopifyProductsClientTests
{
    private const string Token = "shpat_products_secret";

    private static readonly ShopifyConnectionRef Connection =
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "954889", "mi-tienda.myshopify.com", true, Token);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadShopReadsCurrencyTaxesAndCount()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """
            {"data":{"shop":{"currencyCode":"COP","taxesIncluded":false},"productsCount":{"count":1200}}}
            """));

        var shop = await bed.Client.ReadShopAsync(Connection, Ct);

        Assert.Equal(new ShopifyShopInfo("COP", false, 1200), shop);
        var request = Assert.Single(bed.Handler.Requests);
        Assert.Equal("https://mi-tienda.myshopify.com/admin/api/2026-10/graphql.json", request.Uri.AbsoluteUri);
        Assert.Equal(Token, request.Token);
        Assert.Equal("status:active OR status:draft", Variable(request.Body, "query"));
    }

    // Spec 2026-11-17 (onboarding) §4.3: «¿Cobras impuestos?» se sugiere con una sola variante gravada.
    [Fact]
    public async Task TheTaxableQueryAsksForOneTaxableVariant()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """
            {"data":{"productVariants":{"nodes":[{"id":"gid://shopify/ProductVariant/1"}]}}}
            """));

        var taxable = await bed.Client.HasTaxableVariantAsync(Connection, Ct);

        Assert.True(taxable);
        var request = Assert.Single(bed.Handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Contains(
            "productVariants(first: 1, query: \"taxable:true\")",
            body.RootElement.GetProperty("query").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(Token, request.Token);
    }

    [Fact]
    public async Task NoTaxableVariantIsFalse()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """{"data":{"productVariants":{"nodes":[]}}}"""));

        Assert.False(await bed.Client.HasTaxableVariantAsync(Connection, Ct));
    }

    [Fact]
    public async Task AnUnreadableTaxableAnswerIsUnavailable()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """{"data":{"productVariants":null}}"""));

        var error = await Assert.ThrowsAsync<ShopifyProductsSourceException>(() => bed.Client.HasTaxableVariantAsync(Connection, Ct));

        Assert.Equal(ShopifyProductsSourceFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task FetchProductsReadsThePageAndItsCursor()
    {
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "products", "products-page.json"));
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, page));

        var result = await bed.Client.FetchProductsAsync(Connection, "cursor-0", Ct);

        Assert.Equal([7001L, 7002L], result.Products.Select(product => product.Id));
        Assert.Equal("cursor-page-1", result.NextCursor);
        Assert.Equal("cursor-0", Variable(Assert.Single(bed.Handler.Requests).Body, "after"));
    }

    [Fact]
    public async Task AProductWithMoreThan50VariantsIsReadWhole()
    {
        var bed = new Bed((_, body) => body.Contains("query ProductVariants(", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, VariantsPage(50, 20, hasNextPage: false, endCursor: "c2"))
            : Json(HttpStatusCode.OK, ProductResponse(variantCount: 50, hasNextPage: true, endCursor: "c1")));

        var product = await bed.Client.ReadProductAsync(Connection, 7001L, Ct);

        Assert.Equal(70, product!.Variants.Count);
        Assert.Equal(70, product.Variants.Select(variant => variant.Id).Distinct().Count());
        Assert.Equal("gid://shopify/Product/7001", Variable(bed.Handler.Requests[0].Body, "id"));
        Assert.Equal("c1", Variable(bed.Handler.Requests[1].Body, "after"));
    }

    [Fact]
    public async Task ARepeatedVariantsCursorIsUnavailable()
    {
        var bed = new Bed((_, body) => body.Contains("query ProductVariants(", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, VariantsPage(50, 5, hasNextPage: true, endCursor: "c1"))
            : Json(HttpStatusCode.OK, ProductResponse(variantCount: 50, hasNextPage: true, endCursor: "c1")));

        var error = await Assert.ThrowsAsync<ShopifyProductsSourceException>(() => bed.Client.ReadProductAsync(Connection, 7001L, Ct));

        Assert.Equal(ShopifyProductsSourceFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task AMissingProductIsNull()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """{"data":{"product":null}}"""));

        Assert.Null(await bed.Client.ReadProductAsync(Connection, 7001L, Ct));
    }

    [Fact]
    public async Task AccessDeniedIsItsOwnFailure()
    {
        var accessDenied = new Bed((_, _) => Json(HttpStatusCode.OK, """{"errors":[{"message":"Access denied for products field.","extensions":{"code":"ACCESS_DENIED"}}]}"""));
        var forbidden = new Bed((_, _) => Json(HttpStatusCode.Forbidden, """{"errors":"Forbidden"}"""));

        var errors = new[]
        {
            await Assert.ThrowsAsync<ShopifyProductsSourceException>(() => accessDenied.Client.ReadShopAsync(Connection, Ct)),
            await Assert.ThrowsAsync<ShopifyProductsSourceException>(() => forbidden.Client.ReadShopAsync(Connection, Ct)),
        };

        Assert.All(errors, error => Assert.Equal(ShopifyProductsSourceFailure.AccessDenied, error.Failure));
    }

    [Fact]
    public async Task UnauthorizedIsRejected()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.Unauthorized, """{"errors":"[API] Invalid API key or access token"}"""));

        var error = await Assert.ThrowsAsync<ShopifyProductsSourceException>(() => bed.Client.FetchProductsAsync(Connection, null, Ct));

        Assert.Equal(ShopifyProductsSourceFailure.Rejected, error.Failure);
        Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid API key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrottledIsThrottledWithTheWait()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """
            {"errors":[{"message":"Throttled","extensions":{"code":"THROTTLED"}}],
             "extensions":{"cost":{"requestedQueryCost":112,"throttleStatus":{"currentlyAvailable":12,"restoreRate":50.0}}}}
            """));

        var error = await Assert.ThrowsAsync<ShopifyProductsSourceException>(() => bed.Client.FetchProductsAsync(Connection, null, Ct));

        Assert.Equal((ShopifyProductsSourceFailure.Throttled, (TimeSpan?)TimeSpan.FromSeconds(2)), (error.Failure, error.RetryAfter));
    }

    [Fact]
    public async Task VariantKeysReadIdSkuAndProductTitle()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """
            {"data":{"productVariants":{"pageInfo":{"hasNextPage":false,"endCursor":"k1"},
              "nodes":[{"id":"gid://shopify/ProductVariant/4455","sku":" CAM-S-R ","product":{"title":"Camiseta"}},
                       {"id":"gid://shopify/ProductVariant/4456","sku":"","product":{"title":"Gorra"}}]}}}
            """));

        var page = await bed.Client.ListVariantKeysAsync(Connection, null, Ct);

        Assert.Equal(
            [new ShopifyVariantKey(4455L, " CAM-S-R ", "Camiseta"), new ShopifyVariantKey(4456L, null, "Gorra")],
            page.Variants);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ConnectionWithoutTokenIsRefusedWithoutCallingShopify()
    {
        var bed = new Bed((_, _) => throw new InvalidOperationException("Shopify must not be called."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bed.Client.ReadShopAsync(Connection with { AccessToken = null }, Ct));

        Assert.Empty(bed.Handler.Requests);
    }

    private static string ProductResponse(int variantCount, bool hasNextPage, string? endCursor) =>
        new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["product"] = new JsonObject
                {
                    ["id"] = "gid://shopify/Product/7001",
                    ["title"] = "Camiseta",
                    ["status"] = "ACTIVE",
                    ["updatedAt"] = "2026-11-10T15:00:00Z",
                    ["options"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "Talla",
                        ["optionValues"] = new JsonArray(new JsonObject { ["name"] = "Única" }),
                    }),
                    ["featuredMedia"] = null,
                    ["variants"] = VariantsConnection(0, variantCount, hasNextPage, endCursor),
                },
            },
        }.ToJsonString();

    private static string VariantsPage(int start, int count, bool hasNextPage, string? endCursor) =>
        new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["product"] = new JsonObject { ["variants"] = VariantsConnection(start, count, hasNextPage, endCursor) },
            },
        }.ToJsonString();

    private static JsonObject VariantsConnection(int start, int count, bool hasNextPage, string? endCursor) =>
        new()
        {
            ["pageInfo"] = new JsonObject { ["hasNextPage"] = hasNextPage, ["endCursor"] = endCursor },
            ["nodes"] = new JsonArray([.. Enumerable.Range(start, count).Select(index => (JsonNode)new JsonObject
            {
                ["id"] = $"gid://shopify/ProductVariant/{10_000 + index}",
                ["sku"] = $"SKU-{index}",
                ["barcode"] = null,
                ["price"] = "1000.00",
                ["taxable"] = true,
                ["selectedOptions"] = new JsonArray(new JsonObject { ["name"] = "Talla", ["value"] = "Única" }),
            })]),
        };

    private static string? Variable(string body, string name)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("variables", out var variables)
            && variables.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Bed
    {
        public Bed(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        {
            Handler = new FakeShopifyHandler(respond);
            Client = new ShopifyProductsClient(
                new FakeHttpClientFactory(Handler), new FixedApiVersion("2026-10"), new FakeTimeProvider(), NullLogger<ShopifyProductsClient>.Instance);
        }

        public FakeShopifyHandler Handler { get; }

        public ShopifyProductsClient Client { get; }
    }
}
