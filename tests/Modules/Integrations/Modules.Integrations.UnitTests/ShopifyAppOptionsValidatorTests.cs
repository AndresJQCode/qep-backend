using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.Shopify;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-15 §4.2: en Production exige <c>ClientId</c>, <c>ClientSecret</c>, <c>WebhookCallbackUrl</c>
/// (https absoluta) y al menos un origen; en cualquier ambiente, la forma de <c>ApiVersion</c>, de los orígenes y de la
/// URL. Ningún mensaje lleva un valor.</summary>
public sealed class ShopifyAppOptionsValidatorTests
{
    private const string Secret = "shopify-client-secret-SENTINEL-4b2a";

    private static ShopifyAppOptionsValidator Validator(bool production) =>
        new(new UnitTestHostEnvironment(production ? Environments.Production : Environments.Development));

    private static ShopifyAppOptions Complete() => new()
    {
        ClientId = "0123456789abcdef0123456789abcdef",
        ClientSecret = Secret,
        ApiVersion = "2026-10",
        Scopes = "read_orders,read_customers",
        AllowedRedirectOrigins = ["https://qep.qcode.co"],
        WebhookCallbackUrl = "https://qep-api.qcode.co/api/webhooks/shopify",
    };

    [Fact]
    public void ProductionAcceptsTheCompleteSection() =>
        Assert.True(Validator(production: true).Validate(null, Complete()).Succeeded);

    [Fact]
    public void ProductionWithoutClientSecretFails()
    {
        var options = Complete();
        options.ClientSecret = " ";

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains("Shopify:App:ClientSecret", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionWithoutClientIdCallbackOrOriginsNamesEachKey()
    {
        var options = Complete();
        options.ClientId = null;
        options.WebhookCallbackUrl = null;
        options.AllowedRedirectOrigins = [];

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures!.Count());
        Assert.Contains(result.Failures!, failure => failure.Contains("Shopify:App:ClientId", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Shopify:App:WebhookCallbackUrl", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Shopify:App:AllowedRedirectOrigins", StringComparison.Ordinal));
        Assert.All(result.Failures!, failure => Assert.DoesNotContain(Secret, failure, StringComparison.Ordinal));
    }

    // Si SHOPIFY_APP_CLIENT_ID o SHOPIFY_APP_CLIENT_SECRET faltan en el grupo Backend-prod, el pipeline deja el token
    // literal en el ConfigMap o el Secret. Un valor no vacío pasaría como configurado y el pod arrancaría con todas las firmas del
    // webhook inválidas (el hueco que CLAUDE.md describe para Instagram). Un token sin resolver es una clave ausente.
    [Fact]
    public void ProductionWithUnresolvedPipelineTokensNamesEachKey()
    {
        var options = Complete();
        options.ClientId = "#{SHOPIFY_APP_CLIENT_ID}#";
        options.ClientSecret = " #{SHOPIFY_APP_CLIENT_SECRET}# ";

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
        Assert.Contains(result.Failures!, failure => failure.Contains("Shopify:App:ClientId", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Shopify:App:ClientSecret", StringComparison.Ordinal));
        Assert.All(result.Failures!, failure => Assert.Contains("pipeline token", failure, StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionHttpCallbackFails()
    {
        var options = Complete();
        options.WebhookCallbackUrl = "http://qep-api.qcode.co/api/webhooks/shopify";

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Shopify:App:WebhookCallbackUrl", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/webhooks/shopify")]
    [InlineData("ftp://qep-api.qcode.co/api/webhooks/shopify")]
    public void ACallbackThatIsNotAbsoluteHttpsFailsInAnyEnvironment(string url)
    {
        var result = Validator(production: false).Validate(null, new ShopifyAppOptions { WebhookCallbackUrl = url });

        Assert.True(result.Failed);
        Assert.Contains("Shopify:App:WebhookCallbackUrl", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-05")]
    [InlineData("2026-1")]
    [InlineData("v2026-10")]
    [InlineData("unstable")]
    [InlineData("2026-13")]
    public void AnyEnvironmentApiVersion202605Fails(string version)
    {
        var result = Validator(production: false).Validate(null, new ShopifyAppOptions { ApiVersion = version });

        Assert.True(result.Failed);
        Assert.Contains("Shopify:App:ApiVersion", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://qep.qcode.co/shopify/callback")]
    [InlineData("qep.qcode.co")]
    [InlineData("ftp://qep.qcode.co")]
    public void AnOriginWithPathOrOtherSchemeFailsInAnyEnvironment(string origin)
    {
        var result = Validator(production: false).Validate(null, new ShopifyAppOptions { AllowedRedirectOrigins = [origin] });

        Assert.True(result.Failed);
        Assert.Contains("AllowedRedirectOrigins:0", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentEmptyPasses() =>
        Assert.True(Validator(production: false).Validate(null, new ShopifyAppOptions()).Succeeded);

    [Fact]
    public void TheSettingsExposeWhatTheFlowNeeds()
    {
        var options = Complete();
        options.Scopes = " read_orders , read_customers ,";
        options.AllowedRedirectOrigins = ["https://qep.qcode.co", "http://localhost:3002"];
        var settings = new ShopifyAppSettings(Microsoft.Extensions.Options.Options.Create(options));

        Assert.True(settings.IsConfigured);
        Assert.Equal("0123456789abcdef0123456789abcdef", settings.ClientId);
        Assert.Equal(Secret, settings.ClientSecret);
        Assert.Equal("2026-10", settings.ApiVersion);
        Assert.Equal(["read_orders", "read_customers"], settings.Scopes);
        Assert.Equal(new Uri("https://qep-api.qcode.co/api/webhooks/shopify"), settings.WebhookCallbackUrl);
        Assert.True(settings.IsRedirectAllowed(new Uri("https://qep.qcode.co/shopify/callback")));
        Assert.True(settings.IsRedirectAllowed(new Uri("http://localhost:3002/shopify/callback")));
        Assert.False(settings.IsRedirectAllowed(new Uri("https://evil.example/shopify/callback")));
        Assert.False(settings.IsRedirectAllowed(new Uri("http://qep.qcode.co/shopify/callback")));
    }

    // Spec §4.2, D-M3 aplicado a Shopify: sin el par de la app (sólo fuera de Production) no hay cómo instalar.
    [Fact]
    public void AnEmptySectionIsNotConfiguredAndFallsBackToTheDefaults()
    {
        var settings = new ShopifyAppSettings(Microsoft.Extensions.Options.Options.Create(new ShopifyAppOptions()));

        Assert.False(settings.IsConfigured);
        Assert.Null(settings.ClientId);
        Assert.Null(settings.ClientSecret);
        Assert.Null(settings.WebhookCallbackUrl);
        Assert.Equal("2026-10", settings.ApiVersion);
        Assert.Equal(["read_orders", "read_customers"], settings.Scopes);
        Assert.False(settings.IsRedirectAllowed(new Uri("http://localhost:3002/shopify/callback")));
    }
}
