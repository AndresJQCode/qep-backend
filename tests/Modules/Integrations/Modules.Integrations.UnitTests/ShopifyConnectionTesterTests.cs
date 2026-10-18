using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Shopify;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.1, el probador de <c>shopify</c>: con el token guardado lee <c>shop { id name myshopifyDomain }</c>
/// y las suscripciones. 200 refresca <c>shopName</c> y vuelve a crear los topics que falten (y repone
/// <c>webhookSubscriptionIds</c>); 401 y 402 dejan <c>credentials_rejected</c>; red o 5xx, <c>provider_unreachable</c>.
/// </summary>
public sealed class ShopifyConnectionTesterTests
{
    private const string Shop = "mi-tienda.myshopify.com";

    private const string Token = "shpat_tester-token-SENTINEL-1c2d";

    private static readonly Uri Callback = new("https://qep-api.qcode.co/api/webhooks/shopify");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Fields() => new(StringComparer.Ordinal)
    {
        [ShopifyFieldKeys.ShopDomain] = Shop,
        [ShopifyFieldKeys.ShopName] = "Nombre viejo",
        [ShopifyFieldKeys.WebhookSubscriptionIds] = "gid://shopify/WebhookSubscription/1",
    };

    private static Dictionary<string, string> Secrets() => new(StringComparer.Ordinal) { [ShopifyFieldKeys.AccessToken] = Token };

    private static ShopifyConnectionTester Tester(FakeShopifyAdminGateway gateway, FakeShopifyAppSettings? app = null) =>
        new(gateway, app ?? new FakeShopifyAppSettings());

    [Fact]
    public async Task Test200RefreshesShopNameAndRecreatesMissingTopics()
    {
        var gateway = new FakeShopifyAdminGateway { ShopName = "Mi Tienda Renovada" };
        foreach (var (id, topic) in new[] { ("1", "ORDERS_CREATE"), ("2", "ORDERS_UPDATED"), ("3", "ORDERS_PAID") })
        {
            gateway.ExistingWebhooks.Add(new ShopifyWebhookSubscription($"gid://shopify/WebhookSubscription/{id}", topic, Callback));
        }

        var result = await Tester(gateway).TestAsync(Fields(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Equal(["shop", "list-webhooks", "create-webhook:orders/cancelled", "create-webhook:app/uninstalled"], gateway.Calls);
        Assert.All(gateway.Tokens, token => Assert.Equal(Token, token));
        Assert.Equal((Shop, Token), gateway.LastShopRead);
        Assert.NotNull(result.RefreshedFields);
        Assert.Equal("Mi Tienda Renovada", result.RefreshedFields[ShopifyFieldKeys.ShopName]);
        var ids = result.RefreshedFields[ShopifyFieldKeys.WebhookSubscriptionIds].Split(',');
        Assert.Equal(5, ids.Length);
        Assert.Equal(
            ["gid://shopify/WebhookSubscription/1", "gid://shopify/WebhookSubscription/2", "gid://shopify/WebhookSubscription/3"],
            ids[..3]);
        Assert.Equal(IntegrationProviders.Shopify.Key, Tester(gateway).ProviderKey);
    }

    [Fact]
    public async Task Test200WithEveryTopicRegisteredCreatesNothing()
    {
        var gateway = new FakeShopifyAdminGateway();
        var index = 0;
        foreach (var topic in ShopifyWebhookTopics.Registered)
        {
            gateway.ExistingWebhooks.Add(new ShopifyWebhookSubscription(
                $"gid://shopify/WebhookSubscription/{++index}", ShopifyWebhookTopics.ToGraphQl(topic), Callback));
        }

        var result = await Tester(gateway).TestAsync(Fields(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Equal(["shop", "list-webhooks"], gateway.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    public async Task Test401And402CredentialsRejected(int status)
    {
        var gateway = new FakeShopifyAdminGateway { FailAt = "shop", Failure = ShopifyGatewayFailure.Rejected, FailureStatus = status };

        var result = await Tester(gateway).TestAsync(Fields(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, result.Outcome);
        Assert.Equal(ConnectionFailureCodes.CredentialsRejected, result.FailureCode);
        Assert.Equal(["shop"], gateway.Calls);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(null)]
    public async Task Test5xxProviderUnreachable(int? status)
    {
        var gateway = new FakeShopifyAdminGateway { FailAt = "shop", Failure = ShopifyGatewayFailure.Unreachable, FailureStatus = status };

        var result = await Tester(gateway).TestAsync(Fields(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.DoesNotContain(Token, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    // Shopify rechazó crear un topic: la conexión no recibe pedidos, así que no es Ok. El enlace dejó el mismo código.
    [Fact]
    public async Task TestWebhookRejectedIsWebhooksNotRegistered()
    {
        var gateway = new FakeShopifyAdminGateway { FailAt = "create-webhook" };

        var result = await Tester(gateway).TestAsync(Fields(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, result.Outcome);
        Assert.Equal(ConnectionFailureCodes.WebhooksNotRegistered, result.FailureCode);
    }

    [Fact]
    public async Task TestWebhookUnreachableIsProviderUnreachable()
    {
        var gateway = new FakeShopifyAdminGateway { FailAt = "list-webhooks", Failure = ShopifyGatewayFailure.Unreachable };

        var result = await Tester(gateway).TestAsync(Fields(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task TestWithoutTokenOrDomainIsCredentialsRejectedWithoutCallingShopify()
    {
        var gateway = new FakeShopifyAdminGateway();

        var withoutToken = await Tester(gateway).TestAsync(Fields(), new Dictionary<string, string>(), Ct);
        var withoutDomain = await Tester(gateway).TestAsync(new Dictionary<string, string>(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, withoutToken.Outcome);
        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, withoutDomain.Outcome);
        Assert.Empty(gateway.Calls);
    }

    // Local sin túnel: no hay URL que registrar; sólo se refresca el nombre y los ids guardados no se tocan.
    [Fact]
    public async Task TestWithoutCallbackUrlOnlyRefreshesTheName()
    {
        var gateway = new FakeShopifyAdminGateway { ShopName = "Mi Tienda" };

        var result = await Tester(gateway, new FakeShopifyAppSettings { WebhookCallbackUrl = null }).TestAsync(Fields(), Secrets(), Ct);

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Equal(["shop"], gateway.Calls);
        Assert.NotNull(result.RefreshedFields);
        Assert.False(result.RefreshedFields.ContainsKey(ShopifyFieldKeys.WebhookSubscriptionIds));
    }

    [Theory]
    [InlineData("orders/create", "ORDERS_CREATE")]
    [InlineData("app/uninstalled", "APP_UNINSTALLED")]
    [InlineData("ORDERS_PAID", "ORDERS_PAID")]
    public void ToGraphQlTranslatesTheRestTopic(string topic, string expected) =>
        Assert.Equal(expected, ShopifyWebhookTopics.ToGraphQl(topic));
}
