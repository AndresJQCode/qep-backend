using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>Plan 2026-11-12, C-1 y C-2: la clave de Anthropic se guarda como conexión de Integrations (probada con <c>GET /v1/models</c>
/// contra un Anthropic de mentira) y <c>GET /ai/settings</c> la ve, sin la clave.</summary>
public sealed class AiSettingsApiTests
{
    private const string ApiKey = "sk-ant-api03-SENTINEL-k9Qz";

    private static readonly string[] ConnectionManage = ["integrations.connection.read", "integrations.connection.manage"];

    [Fact]
    public async Task WithoutKeysEveryProviderIsListedUnconnected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var settings = await client.GetFromJsonAsync<AiSettingsDto>($"{AiUrl()}/settings", TestContext.Current.CancellationToken);

        Assert.NotNull(settings);
        Assert.Equal(
            [new AiProviderSettingsDto("anthropic", true, null, null), new AiProviderSettingsDto("openai", true, null, null)],
            settings.Providers);
    }

    // Tarea 6 (spec §9.1, O-10): la clave de OpenAI se prueba con GET /v1/models y se ve igual que la de Anthropic, sin la clave.
    [Fact]
    public async Task ASavedOpenAiKeyIsTestedAndShowsUpActiveWithoutTheKey()
    {
        const string openAiKey = "sk-proj-SENTINEL-m4Tq";
        var openAi = new FakeOpenAiModelsHandler(openAiKey);
        var database = await StartDatabaseAsync();
        await using var _ = database;
        using var factory = new QepApiFactory(
            database.GetConnectionString(),
            services => services.AddHttpClient(OpenAiTesterClient).ConfigurePrimaryHttpMessageHandler(() => openAi));
        await SeedTenantAsync(factory);
        using var manager = CreateClient(factory, permissions: [.. ConnectionManage, AiPermissions.AgentRead]);
        manager.DefaultRequestHeaders.Add("X-Qep-Client", "web");
        var ct = TestContext.Current.CancellationToken;

        var created = await manager.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/integrations/connections",
            new { providerKey = "openai", name = "Cuenta de OpenAI", fields = new { }, secrets = new { apiKey = openAiKey } },
            ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("m4Tq", connection.GetProperty("secrets").GetProperty("apiKey").GetProperty("hint").GetString());
        Assert.Equal((HttpMethod.Get, "https://api.openai.com/v1/models", true), Assert.Single(openAi.Requests));

        var response = await manager.GetAsync($"{AiUrl()}/settings", ct);
        var body = await BodyAsync(response);
        var settings = JsonSerializer.Deserialize<AiSettingsDto>(body, JsonSerializerOptions.Web);
        Assert.NotNull(settings);
        Assert.Equal(new AiProviderSettingsDto("openai", true, connection.GetProperty("id").GetGuid(), "Active"), settings.Providers[1]);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", connection.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASavedAnthropicKeyIsTestedAndShowsUpActiveWithoutTheKey()
    {
        var anthropic = new FakeAnthropicHandler(ApiKey);
        var database = await StartDatabaseAsync();
        await using var _ = database;
        using var factory = new QepApiFactory(
            database.GetConnectionString(),
            services => services.AddHttpClient(AnthropicTesterClient).ConfigurePrimaryHttpMessageHandler(() => anthropic));
        await SeedTenantAsync(factory);
        using var manager = CreateClient(factory, permissions: [.. ConnectionManage, AiPermissions.AgentRead]);
        manager.DefaultRequestHeaders.Add("X-Qep-Client", "web");
        var ct = TestContext.Current.CancellationToken;

        var created = await manager.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/integrations/connections",
            new { providerKey = "anthropic", name = "Cuenta de Anthropic", fields = new { }, secrets = new { apiKey = ApiKey } },
            ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("k9Qz", connection.GetProperty("secrets").GetProperty("apiKey").GetProperty("hint").GetString());
        var request = Assert.Single(anthropic.Requests);
        Assert.Equal((HttpMethod.Get, "https://api.anthropic.com/v1/models", true), request);

        var response = await manager.GetAsync($"{AiUrl()}/settings", ct);
        var body = await BodyAsync(response);
        var settings = JsonSerializer.Deserialize<AiSettingsDto>(body, JsonSerializerOptions.Web);
        Assert.NotNull(settings);
        Assert.Equal(
            new AiProviderSettingsDto("anthropic", true, connection.GetProperty("id").GetGuid(), "Active"),
            settings.Providers[0]);
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", connection.GetRawText(), StringComparison.Ordinal);
    }

    // C-2: una clave que Anthropic rechaza no se guarda.
    [Fact]
    public async Task ARejectedAnthropicKeyIsNotSaved()
    {
        var anthropic = new FakeAnthropicHandler(ApiKey) { Status = HttpStatusCode.Unauthorized };
        var database = await StartDatabaseAsync();
        await using var _ = database;
        using var factory = new QepApiFactory(
            database.GetConnectionString(),
            services => services.AddHttpClient(AnthropicTesterClient).ConfigurePrimaryHttpMessageHandler(() => anthropic));
        await SeedTenantAsync(factory);
        using var manager = CreateClient(factory, permissions: [.. ConnectionManage, AiPermissions.AgentRead]);
        manager.DefaultRequestHeaders.Add("X-Qep-Client", "web");
        var ct = TestContext.Current.CancellationToken;

        var created = await manager.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/integrations/connections",
            new { providerKey = "anthropic", name = "Cuenta de Anthropic", fields = new { }, secrets = new { apiKey = ApiKey } },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
        Assert.Contains("integrations.connection.credentials_rejected", await BodyAsync(created), StringComparison.Ordinal);
        var settings = await manager.GetFromJsonAsync<AiSettingsDto>($"{AiUrl()}/settings", ct);
        Assert.Null(settings!.Providers[0].ConnectionId);
    }
}
