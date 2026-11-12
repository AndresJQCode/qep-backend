using System.Net;
using System.Net.Http.Json;
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
        // Tarea 6: hasta que exista el adaptador de OpenAI, sus modelos se listan sin poder elegirse.
        Assert.All(catalog.Providers[1].Models, model => Assert.False(model.Available));
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
