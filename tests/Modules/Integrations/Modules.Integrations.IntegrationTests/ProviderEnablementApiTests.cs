using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Plan 2026-10-21 (proveedores habilitados por tenant), Task 4: Zenvia necesita, además de <c>quotations</c>, la fila
/// <c>active</c> en <c>tenancy.tenant_providers</c>. Deshabilitado con conexiones, el tenant las sigue viendo —marcadas—
/// y puede borrarlas; no puede crear, editar, probar, pausar ni reanudar, y los consumidores no las resuelven. Los
/// campos nuevos se leen del JSON para fijar también el nombre en camelCase que lee la pantalla.
/// </summary>
public sealed class ProviderEnablementApiTests
{
    private const string NotEnabledCode = "integrations.provider.not_enabled";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement?> CatalogProviderAsync(HttpClient client, Guid tenantId, string providerKey)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(CatalogUrl(tenantId), Ct));
        foreach (var provider in document.RootElement.GetProperty("providers").EnumerateArray())
        {
            if (provider.GetProperty("key").GetString() == providerKey)
            {
                return provider.Clone();
            }
        }

        return null;
    }

    [Fact]
    public async Task TheCatalogHidesZenviaWhenItIsNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        Assert.Null(await CatalogProviderAsync(client, tenant.TenantId, "zenvia"));
        // Lo que no exige habilitación sigue igual: shopify viene con el signup y la app está fijada en el harness.
        var shopify = await CatalogProviderAsync(client, tenant.TenantId, "shopify");
        Assert.NotNull(shopify);
        Assert.True(shopify.Value.GetProperty("platformEnabled").GetBoolean());
    }

    [Fact]
    public async Task TheCatalogOffersZenviaOnceEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableProviderAsync(connectionString, tenant.TenantId, "zenvia");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var zenvia = await CatalogProviderAsync(client, tenant.TenantId, "zenvia");

        Assert.NotNull(zenvia);
        Assert.True(zenvia.Value.GetProperty("platformEnabled").GetBoolean());
        Assert.Equal(0, zenvia.Value.GetProperty("connectionCount").GetInt32());
    }

    [Fact]
    public async Task ADisabledZenviaWithConnectionsStaysListed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterZenviaTenantAsync(factory, connectionString);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);

        await DisableProviderAsync(connectionString, tenant.TenantId, "zenvia");

        var zenvia = await CatalogProviderAsync(client, tenant.TenantId, "zenvia");
        Assert.NotNull(zenvia);
        Assert.False(zenvia.Value.GetProperty("platformEnabled").GetBoolean());
        Assert.Equal(1, zenvia.Value.GetProperty("connectionCount").GetInt32());

        using var list = JsonDocument.Parse(await client.GetStringAsync(ConnectionsUrl(tenant.TenantId), Ct));
        var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(created.Id, item.GetProperty("id").GetGuid());
        Assert.False(item.GetProperty("providerEnabled").GetBoolean());

        // Leer una conexión no exige la habilitación.
        var read = await SendAsync(client, HttpMethod.Get, ConnectionUrl(tenant.TenantId, created.Id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var single = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        Assert.False(single.RootElement.GetProperty("providerEnabled").GetBoolean());
    }

    [Fact]
    public async Task AnEnabledProviderMarksItsConnectionsAsEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterZenviaTenantAsync(factory, connectionString);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(created.RootElement.GetProperty("providerEnabled").GetBoolean());
        using var list = JsonDocument.Parse(await client.GetStringAsync(ConnectionsUrl(tenant.TenantId), Ct));
        Assert.True(Assert.Single(list.RootElement.GetProperty("items").EnumerateArray()).GetProperty("providerEnabled").GetBoolean());
    }

    [Fact]
    public async Task CreatingAZenviaConnectionWhileDisabledIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(NotEnabledCode, (await ProblemAsync(response)).Code);
        // Ni una prueba contra Zenvia ni una fila: el chequeo va antes que el probador.
        Assert.Empty(factory.ZenviaHandler.Requests);
        Assert.Equal(0L, await CountConnectionsAsync(connectionString, tenant.TenantId));
    }

    [Fact]
    public async Task EditingTestingPausingResumingAreRefusedWhileDisabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterZenviaTenantAsync(factory, connectionString);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var active = await CreateConnectionAsync(client, tenant.TenantId, "Activa");
        var paused = await CreateConnectionAsync(client, tenant.TenantId, "Pausada");
        (await SendAsync(client, HttpMethod.Post, $"{ConnectionUrl(tenant.TenantId, paused.Id)}/pause", ifMatch: "\"1\""))
            .EnsureSuccessStatusCode();
        var zenviaCalls = factory.ZenviaHandler.Requests.Count;

        await DisableProviderAsync(connectionString, tenant.TenantId, "zenvia");

        var activeUrl = ConnectionUrl(tenant.TenantId, active.Id);
        var edit = await SendAsync(client, HttpMethod.Put, activeUrl, ZenviaBody("Renombrada"), ifMatch: "\"1\"");
        var test = await SendAsync(client, HttpMethod.Post, $"{activeUrl}/test");
        var pause = await SendAsync(client, HttpMethod.Post, $"{activeUrl}/pause", ifMatch: "\"1\"");
        var resume = await SendAsync(client, HttpMethod.Post, $"{ConnectionUrl(tenant.TenantId, paused.Id)}/resume", ifMatch: "\"2\"");

        foreach (var response in new[] { edit, test, pause, resume })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(NotEnabledCode, (await ProblemAsync(response)).Code);
        }

        Assert.Equal(zenviaCalls, factory.ZenviaHandler.Requests.Count);
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT version FROM integrations.connections WHERE id = @id",
            ("id", active.Id)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM integrations.connections WHERE id = @id AND name <> 'Activa'",
            ("id", active.Id)));
    }

    [Fact]
    public async Task DeletingStillWorksWhileDisabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterZenviaTenantAsync(factory, connectionString);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);

        await DisableProviderAsync(connectionString, tenant.TenantId, "zenvia");
        var deleted = await SendAsync(client, HttpMethod.Delete, ConnectionUrl(tenant.TenantId, created.Id), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0L, await CountConnectionsAsync(connectionString, tenant.TenantId));
    }

    // El orden de los chequeos importa: con el módulo apagado la respuesta sigue siendo la de siempre.
    [Fact]
    public async Task AModuleOffStillAnswersModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await SetModuleStatusAsync(connectionString, tenant.TenantId, "quotations", "inactive");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ResolveSkipsADisabledProvider()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterZenviaTenantAsync(factory, connectionString);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);

        await DisableProviderAsync(connectionString, tenant.TenantId, "zenvia");

        using var scope = factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IIntegrationConnections>();
        Assert.Null((await connections.ResolveAsync(tenant.TenantId, created.Id, Ct)).Connection);
        Assert.Empty(await connections.ListActiveAsync(tenant.TenantId, "zenvia", Ct));
        // ListByProviderAsync no filtra por visibilidad ni por habilitación: la pantalla del consumidor nombra lo que existe.
        Assert.Equal([created.Id], (await connections.ListByProviderAsync(tenant.TenantId, "zenvia", Ct)).Select(listing => listing.Id));
    }
}
