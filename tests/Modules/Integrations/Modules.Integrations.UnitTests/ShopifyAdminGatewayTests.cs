using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Shopify;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.3 paso 4 y §4.1 contra Shopify, sobre el cliente nombrado <c>integrations.shopify-admin</c>: el canje
/// es <c>POST https://{shop}/admin/oauth/access_token</c> con <c>client_id</c>, <c>client_secret</c> y <c>code</c>; la Admin
/// API es <c>POST https://{shop}/admin/api/{version}/graphql.json</c> con <c>X-Shopify-Access-Token</c>. 401/402/403 y el
/// resto de 4xx son <see cref="ShopifyGatewayFailure.Rejected"/>; 429, 5xx, timeout y red,
/// <see cref="ShopifyGatewayFailure.Unreachable"/>; un 200 que no se puede leer o no cabe, <see cref="ShopifyGatewayFailure.Invalid"/>.
/// Nunca registra el token, el <c>code</c> ni el client secret.
/// </summary>
public sealed class ShopifyAdminGatewayTests : IDisposable
{
    private const string Shop = "mi-tienda.myshopify.com";

    private const string ClientSecret = "shopify-client-secret-SENTINEL-gw";

    private const string Code = "shopify-code-SENTINEL-gw";

    private const string Token = "shpat_token-SENTINEL-gw";

    private readonly StubShopifyHandler _shopify = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public ShopifyAdminGatewayTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Shopify:App:ClientId"] = "shopify-client-id-gw",
                ["Shopify:App:ClientSecret"] = ClientSecret,
                ["Shopify:App:ApiVersion"] = "2026-10",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment());
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(ShopifyAdminGateway.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _shopify);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IShopifyAdminGateway Gateway => _services.GetRequiredService<IShopifyAdminGateway>();

    [Fact]
    public async Task ExchangeCodePostsClientIdSecretCode()
    {
        _shopify.Body = $$"""{"access_token":"{{Token}}","scope":"read_orders,read_customers"}""";

        var grant = await Gateway.ExchangeCodeAsync(Shop, Code, Ct);

        Assert.Equal(Token, grant.AccessToken);
        Assert.Equal("read_orders,read_customers", grant.Scopes);
        var request = Assert.Single(_shopify.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://mi-tienda.myshopify.com/admin/oauth/access_token", request.Uri!.ToString());
        Assert.Null(request.AccessToken);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("shopify-client-id-gw", body.RootElement.GetProperty("client_id").GetString());
        Assert.Equal(ClientSecret, body.RootElement.GetProperty("client_secret").GetString());
        Assert.Equal(Code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal("application/json", request.ContentType);
        AssertNothingSecretLogged();
    }

    [Fact]
    public async Task GetShopSendsXShopifyAccessTokenToGraphQlVersionedUrl()
    {
        _shopify.Body = """{"data":{"shop":{"id":"gid://shopify/Shop/12345678","name":"Mi Tienda","myshopifyDomain":"mi-tienda.myshopify.com"}}}""";

        var shop = await Gateway.GetShopAsync(Shop, Token, Ct);

        Assert.Equal(new ShopifyShopInfo("12345678", "Mi Tienda", Shop), shop);
        var request = Assert.Single(_shopify.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://mi-tienda.myshopify.com/admin/api/2026-10/graphql.json", request.Uri!.ToString());
        Assert.Equal(Token, request.AccessToken);
        Assert.Null(request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Contains("shop", body.RootElement.GetProperty("query").GetString(), StringComparison.Ordinal);
        AssertNothingSecretLogged();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Status401MapsToRejected(HttpStatusCode status)
    {
        _shopify.Status = status;
        _shopify.Body = $$"""{"error":"invalid_request","error_description":"{{Code}}"}""";

        var exchange = await Assert.ThrowsAsync<ShopifyGatewayException>(() => Gateway.ExchangeCodeAsync(Shop, Code, Ct));
        var read = await Assert.ThrowsAsync<ShopifyGatewayException>(() => Gateway.GetShopAsync(Shop, Token, Ct));

        Assert.Equal(ShopifyGatewayFailure.Rejected, exchange.Kind);
        Assert.Equal((int)status, exchange.Status);
        Assert.Equal(ShopifyGatewayFailure.Rejected, read.Kind);
        Assert.DoesNotContain(Code, exchange.ToString(), StringComparison.Ordinal);
        AssertNothingSecretLogged();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Status503MapsToUnreachable(HttpStatusCode status)
    {
        _shopify.Status = status;

        var exchange = await Assert.ThrowsAsync<ShopifyGatewayException>(() => Gateway.ExchangeCodeAsync(Shop, Code, Ct));

        Assert.Equal(ShopifyGatewayFailure.Unreachable, exchange.Kind);
        Assert.Equal((int)status, exchange.Status);
    }

    [Fact]
    public async Task NetworkFailureMapsToUnreachableWithoutStatus()
    {
        _shopify.Throw = new HttpRequestException("boom");

        var error = await Assert.ThrowsAsync<ShopifyGatewayException>(() => Gateway.GetShopAsync(Shop, Token, Ct));

        Assert.Equal(ShopifyGatewayFailure.Unreachable, error.Kind);
        Assert.Null(error.Status);
    }

    [Theory]
    [InlineData("""{"scope":"read_orders"}""")]
    [InlineData("""not json""")]
    [InlineData("""{"access_token":"","scope":"read_orders"}""")]
    public async Task ExchangeWithoutATokenIsInvalid(string body)
    {
        _shopify.Body = body;

        var error = await Assert.ThrowsAsync<ShopifyGatewayException>(() => Gateway.ExchangeCodeAsync(Shop, Code, Ct));

        Assert.Equal(ShopifyGatewayFailure.Invalid, error.Kind);
    }

    // Minor (b) de la tarea 6: lo que no cabe en la columna se rechaza acá con un error tipado, no con un 500 de la base.
    [Fact]
    public async Task ExchangeWithScopesLongerThanTheColumnIsInvalid()
    {
        _shopify.Body = $$"""{"access_token":"{{Token}}","scope":"{{new string('a', ShopifyInstall.ScopesMaxLength + 1)}}"}""";

        var error = await Assert.ThrowsAsync<ShopifyGatewayException>(() => Gateway.ExchangeCodeAsync(Shop, Code, Ct));

        Assert.Equal(ShopifyGatewayFailure.Invalid, error.Kind);
    }

    [Theory]
    [InlineData("""{"data":{"shop":{"id":"gid://shopify/Shop/1234567890123456789012345678901234567890123456789012345678901234567","name":"x","myshopifyDomain":"mi-tienda.myshopify.com"}}}""")]
    [InlineData("""{"data":{"shop":{"id":"","name":"x"}}}""")]
    [InlineData("""{"errors":[{"message":"Access denied"}]}""")]
    [InlineData("""{"data":null}""")]
    public async Task GetShopUnreadableOrTooLongIsInvalid(string body)
    {
        _shopify.Body = body;

        var error = await Assert.ThrowsAsync<ShopifyGatewayException>(() => Gateway.GetShopAsync(Shop, Token, Ct));

        Assert.Equal(ShopifyGatewayFailure.Invalid, error.Kind);
    }

    // El dominio arma el host de la URL: uno que no es de Shopify mandaría el token a otro lado.
    [Fact]
    public async Task AShopOutsideMyShopifyIsRefusedBeforeSending()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway.GetShopAsync("evil.example", Token, Ct));

        Assert.Empty(_shopify.Requests);
    }

    [Fact]
    public async Task ListWebhooksReadsTopicsAndUris()
    {
        _shopify.Body = """
            {"data":{"webhookSubscriptions":{"edges":[
              {"node":{"id":"gid://shopify/WebhookSubscription/1","topic":"ORDERS_CREATE","uri":"https://qep-api.qcode.co/api/webhooks/shopify"}},
              {"node":{"id":"gid://shopify/WebhookSubscription/2","topic":"APP_UNINSTALLED","uri":"https://otra-app.example/webhooks"}}
            ]}}}
            """;

        var subscriptions = await Gateway.ListWebhooksAsync(Shop, Token, Ct);

        Assert.Equal(
            [
                new ShopifyWebhookSubscription("gid://shopify/WebhookSubscription/1", "ORDERS_CREATE", new Uri("https://qep-api.qcode.co/api/webhooks/shopify")),
                new ShopifyWebhookSubscription("gid://shopify/WebhookSubscription/2", "APP_UNINSTALLED", new Uri("https://otra-app.example/webhooks")),
            ],
            subscriptions);
        Assert.Equal(Token, Assert.Single(_shopify.Requests).AccessToken);
    }

    [Fact]
    public async Task CreateWebhookSendsTopicAndUri()
    {
        _shopify.Body = """
            {"data":{"webhookSubscriptionCreate":{"webhookSubscription":{"id":"gid://shopify/WebhookSubscription/9","topic":"ORDERS_CREATE","uri":"https://qep-api.qcode.co/api/webhooks/shopify"},"userErrors":[]}}}
            """;
        var callback = new Uri("https://qep-api.qcode.co/api/webhooks/shopify");

        var created = await Gateway.CreateWebhookAsync(Shop, Token, "ORDERS_CREATE", callback, Ct);

        Assert.Equal(new ShopifyWebhookSubscription("gid://shopify/WebhookSubscription/9", "ORDERS_CREATE", callback), created);
        using var body = JsonDocument.Parse(Assert.Single(_shopify.Requests).Body!);
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal("ORDERS_CREATE", variables.GetProperty("topic").GetString());
        Assert.Equal(callback.ToString(), variables.GetProperty("webhookSubscription").GetProperty("uri").GetString());
        Assert.Equal("JSON", variables.GetProperty("webhookSubscription").GetProperty("format").GetString());
    }

    [Fact]
    public async Task CreateWebhookUserErrorsAreRejected()
    {
        _shopify.Body = """
            {"data":{"webhookSubscriptionCreate":{"webhookSubscription":null,"userErrors":[{"field":["webhookSubscription","uri"],"message":"Address is invalid"}]}}}
            """;

        var error = await Assert.ThrowsAsync<ShopifyGatewayException>(() =>
            Gateway.CreateWebhookAsync(Shop, Token, "ORDERS_CREATE", new Uri("https://qep-api.qcode.co/api/webhooks/shopify"), Ct));

        Assert.Equal(ShopifyGatewayFailure.Rejected, error.Kind);
    }

    private void AssertNothingSecretLogged()
    {
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Code, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientSecret, _logs.AllText, StringComparison.Ordinal);
    }
}

internal sealed record ShopifyRequest(HttpMethod Method, Uri? Uri, string? AccessToken, string? Authorization, string? ContentType, string? Body);

/// <summary>Una sola respuesta para todo lo que salga, anotando el header de Shopify, que <c>StubGraphHandler</c> no ve.</summary>
internal sealed class StubShopifyHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "{}";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<ShopifyRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new ShopifyRequest(
            request.Method,
            request.RequestUri,
            request.Headers.TryGetValues("X-Shopify-Access-Token", out var values) ? values.Single() : null,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }
}
