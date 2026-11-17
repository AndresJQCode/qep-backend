using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using Modules.Tenancy.Domain;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>Plan 2026-11-12, C-3: <c>GET /ai/models</c> con la lista curada de <c>appsettings.json</c>, agrupada por proveedor.</summary>
public sealed class AiModelsApiTests
{
    [Fact]
    public async Task ItListsBothProvidersWithTheCuratedModels()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var catalog = await client.GetFromJsonAsync<AiModelCatalogDto>($"{AiUrl()}/models", TestContext.Current.CancellationToken);

        Assert.NotNull(catalog);
        Assert.Equal("anthropic", catalog.DefaultProvider);
        Assert.Equal(["anthropic", "openai"], catalog.Providers.Select(provider => provider.Provider));
        var anthropic = catalog.Providers[0];
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"], anthropic.Models.Select(model => model.Id));
        Assert.Equal("claude-opus-5-5", Assert.Single(anthropic.Models, model => model.IsDefault).Id);
        Assert.All(anthropic.Models, model => Assert.True(model.Available));
        // Tarea 6: con su adaptador, los modelos de OpenAI (D-4a) se pueden elegir.
        var openAi = catalog.Providers[1];
        Assert.Equal(["gpt-6-luna", "gpt-6.1-terra", "gpt-6.1-sol"], openAi.Models.Select(model => model.Id));
        Assert.Equal("gpt-6-luna", Assert.Single(openAi.Models, model => model.IsDefault).Id);
        Assert.All(openAi.Models, model => Assert.True(model.Available));
    }

    // Plan 2026-11-17 A2 (C-3): con la clave de Anthropic activa y sin la de OpenAI, el selector sabe cuál se puede usar y por qué no la otra.
    [Fact]
    public async Task TheModelsSayWhichProviderIsConfiguredAndTheIssueOfTheOther()
    {
        const string apiKey = "sk-ant-api03-SENTINEL-p2Rw";
        var anthropic = new FakeAnthropicHandler(apiKey);
        var database = await StartDatabaseAsync();
        await using var _ = database;
        using var factory = new QepApiFactory(
            database.GetConnectionString(),
            services => services.AddHttpClient(AnthropicTesterClient).ConfigurePrimaryHttpMessageHandler(() => anthropic));
        await SeedTenantAsync(factory);
        using var manager = CreateClient(
            factory, permissions: ["integrations.connection.read", "integrations.connection.manage", AiPermissions.AgentRead]);
        manager.DefaultRequestHeaders.Add("X-Qep-Client", "web");
        var ct = TestContext.Current.CancellationToken;

        var created = await manager.PostAsJsonAsync(
            $"/api/v1/tenants/{AiApiHarness.TenantId}/integrations/connections",
            new { providerKey = "anthropic", name = "Cuenta de Anthropic", fields = new { }, secrets = new { apiKey } },
            ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var catalog = await manager.GetFromJsonAsync<AiModelCatalogDto>($"{AiUrl()}/models", ct);

        Assert.Equal(
            [("anthropic", true, (string?)null), ("openai", false, "not_configured")],
            catalog!.Providers.Select(provider => (provider.Provider, provider.Configured, provider.Issue)));
    }

    // El esfuerzo es de la llamada al proveedor, no de la pantalla.
    [Fact]
    public async Task TheEffortDoesNotTravel()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var response = await client.GetAsync($"{AiUrl()}/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("effort", await BodyAsync(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithTheModuleOffItIs403ModuleNotEnabled()
    {
        var (database, factory) = await OpenAsync(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Ai));
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var response = await client.GetAsync($"{AiUrl()}/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("tenancy.module_not_enabled", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherTenantIs403AuthorizationDenied()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, permissions: AiPermissions.AgentRead);

        var response = await client.GetAsync($"{AiUrl(OtherTenantId)}/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("authorization.denied", await BodyAsync(response), StringComparison.Ordinal);
    }

    // Sin el permiso responde la política, antes del handler: 403 y no 500 (la política existe).
    [Fact]
    public async Task WithoutThePermissionItIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory);

        var response = await client.GetAsync($"{AiUrl()}/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
