using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Addendum 2026-10-30 del spec 2026-10-29: con <c>Meta:App:WebhookOverrideCallbackUrl</c> la suscripción de la WABA
/// lleva <c>override_callback_uri</c> y <c>verify_token</c> en el cuerpo JSON; sin ella sale exactamente como antes, sin cuerpo.
/// Una suscripción rechazada no se reintenta sin los parámetros: mandaría el historial a la URL de la app.</summary>
public sealed class MetaGraphSignupGatewayWebhookOverrideTests : IDisposable
{
    private const string CallbackUrl = "https://qep-api.qcode.co/api/webhooks/whatsapp";
    private const string VerifyToken = "meta-verify-token-SENTINEL-9e8d-0123456789abcdef";
    private const string AccessToken = "meta-access-token-SENTINEL-55bb";

    private readonly StubGraphHandler _graph = new();
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private Task<GraphResult<bool>> SubscribeAsync(string? overrideUrl, string environment = "Development", string verifyToken = VerifyToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Meta:App:AppId"] = "123",
                ["Meta:App:ConfigId"] = "456",
                ["Meta:App:GraphApiVersion"] = "v24.0",
                ["Meta:App:AppSecret"] = "meta-app-secret-SENTINEL-1a2b",
                ["Meta:App:WebhookVerifyToken"] = verifyToken,
                ["Meta:App:WebhookOverrideCallbackUrl"] = overrideUrl,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment(environment));
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(MetaGraphClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _graph);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IWhatsAppSignupGateway>()
            .SubscribeAppAsync("222", AccessToken, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WithTheSettingBothParametersTravelInTheJsonBody()
    {
        var result = await SubscribeAsync(CallbackUrl);

        Assert.True(result.Succeeded);
        var request = Assert.Single(_graph.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graph.facebook.com/v24.0/222/subscribed_apps", request.Uri!.ToString());
        Assert.Equal($"Bearer {AccessToken}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(CallbackUrl, body.RootElement.GetProperty("override_callback_uri").GetString());
        Assert.Equal(VerifyToken, body.RootElement.GetProperty("verify_token").GetString());
    }

    // Messaging compara contra el token recortado: si aquí viajara con espacios, Meta fallaría el desafío GET.
    [Fact]
    public async Task TheSettingAndTheTokenAreSentTrimmedLikeTheWebhookComparesThem()
    {
        await SubscribeAsync($"  {CallbackUrl}  ", verifyToken: $"  {VerifyToken}  ");

        using var body = JsonDocument.Parse(Assert.Single(_graph.Requests).Body!);
        Assert.Equal(CallbackUrl, body.RootElement.GetProperty("override_callback_uri").GetString());
        Assert.Equal(VerifyToken, body.RootElement.GetProperty("verify_token").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutTheSettingTheRequestIsTheSameAsAlways(string? overrideUrl)
    {
        var result = await SubscribeAsync(overrideUrl);

        Assert.True(result.Succeeded);
        var request = Assert.Single(_graph.Requests);
        Assert.Equal("https://graph.facebook.com/v24.0/222/subscribed_apps", request.Uri!.ToString());
        Assert.Null(request.Body);
    }

    // Lo que deja el pipeline si la variable no está en Backend-prod: es «sin override», no una URL.
    [Fact]
    public async Task AnUnresolvedPipelineTokenInProductionMeansNoOverride()
    {
        var result = await SubscribeAsync("#{META_WEBHOOK_OVERRIDE_CALLBACK_URL}#", Environments.Production);

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(_graph.Requests).Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARejectedOverrideIsAFailedSubscribeAndIsNotRetriedWithoutIt(HttpStatusCode status)
    {
        _graph.Status = status;
        _graph.Body = """{"error":{"message":"x","code":2200,"fbtrace_id":"trace"}}""";

        var result = await SubscribeAsync(CallbackUrl);

        Assert.False(result.Succeeded);
        Assert.NotNull(Assert.Single(_graph.Requests).Body);
    }
}
