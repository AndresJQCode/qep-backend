using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1 y §6, plan Tarea 8: el <c>GET …/setup</c> de punta a punta con el host entero, contra
/// Postgres. La conexión no concede <c>read_products</c>, así que el BFF no le pregunta nada a Shopify.
/// </summary>
public sealed class ShopifySetupApiTests
{
    private const string StoredProfile =
        """{"email":"dueno@mitienda.co","currencyCode":"COP","taxesIncluded":true,"billingAddress":{"company":"Mi Tienda SAS","city":"Medellín","provinceCode":"ANT","countryCode":"CO","phone":"+573001112233"}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SetupRoute(ShopifyWorld world) =>
        $"/api/v1/tenants/{world.TenantId}/shopify/stores/{world.ConnectionId}/setup";

    private static HttpClient ClientOf(QepApiFactory factory, ShopifyWorld world) =>
        CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, ShopifyPermissions.ConnectionRead, ShopifyPermissions.ConnectionManage);

    private static Task SetShopProfileAsync(string connectionString, Guid connectionId, string profile) =>
        ExecuteAsync(
            connectionString,
            "UPDATE integrations.connections SET fields = fields || jsonb_build_object('shopProfile', @profile::text) WHERE id = @id",
            ("profile", profile),
            ("id", connectionId));

    private static JsonObject Step(JsonObject setup, string key) =>
        setup["steps"]!.AsArray().Select(step => step!.AsObject()).Single(step => step["key"]!.GetValue<string>() == key);

    [Fact]
    public async Task GetSetupOfANewStoreCreatesItAndAnswersPending()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await SetShopProfileAsync(connectionString, world.ConnectionId, StoredProfile);
        using var client = ClientOf(factory, world);

        var response = await client.GetAsync(SetupRoute(world), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setup = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal("company", setup["openStep"]!.GetValue<string>());
        Assert.Equal("Pending", Step(setup, "company")["status"]!.GetValue<string>());
        Assert.Equal("Blocked", Step(setup, "advisor")["status"]!.GetValue<string>());
        var suggested = setup["company"]!["suggested"]!;
        Assert.Equal("Mi Tienda SAS", suggested["name"]!.GetValue<string>());
        Assert.NotNull(suggested["cityId"]);
        Assert.Null(suggested["cityName"]);
        Assert.Null(setup["company"]!["suggestedChargesVat"]);
        Assert.Equal(world.Company.Id, Assert.Single(setup["companies"]!.AsArray())!["id"]!.GetValue<Guid>());
        Assert.Empty(setup["waiting"]!.AsArray());
        Assert.False(setup["products"]!["scopeGranted"]!.GetValue<bool>());
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE connection_id = @c", ("c", world.ConnectionId)));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task TheStoreSettingsCarryTheVersionThePutNeeds()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await world.ConfigureStoreAsync(factory);
        using var client = ClientOf(factory, world);

        var store = await client.GetAsync($"/api/v1/tenants/{world.TenantId}/shopify/stores/{world.ConnectionId}", Ct);
        var setup = (await client.GetFromJsonAsync<JsonObject>(SetupRoute(world), Ct))!;

        Assert.Equal(store.Headers.ETag!.Tag, $"\"{setup["store"]!["settings"]!["version"]!.GetValue<long>()}\"");
        Assert.Equal(world.AdvisorMembershipId, setup["store"]!["settings"]!["advisorMembershipId"]!.GetValue<Guid>());
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task WaitingGroupsTheStoreOrdersByReason()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await world.ConfigureStoreAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var mirrors = scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>();
            for (var index = 0; index < 3; index++)
            {
                var now = DateTimeOffset.UtcNow;
                var header = new ShopifyOrderHeader($"#{1000 + index}", null, now.AddMinutes(-5), null, "pending", "COP", 1_000m, false);
                var row = ShopifyOrderMirror.Create(
                    world.TenantId, world.ConnectionId, 7_000L + index, """{"name":"#1001","lines":[]}""", now, header);
                row.MarkWaiting(
                    index < 2 ? ShopifyWaitReasons.ProductUnmatched : ShopifyWaitReasons.AdvisorInactive,
                    index < 2 ? $$"""{"skus":["SKU-{{index}}"],"customTitles":[]}""" : null,
                    DateTimeOffset.UtcNow);
                mirrors.Add(row);
            }

            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var client = ClientOf(factory, world);
        var waiting = (await client.GetFromJsonAsync<JsonObject>(SetupRoute(world), Ct))!["waiting"]!.AsArray();

        Assert.Equal(2, waiting.Count);
        Assert.Equal((ShopifyWaitReasons.ProductUnmatched, 2), (waiting[0]!["reason"]!.GetValue<string>(), waiting[0]!["count"]!.GetValue<int>()));
        Assert.Equal(["SKU-0", "SKU-1"], waiting[0]!["sample"]!.AsArray().Select(sku => sku!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Empty(waiting[1]!["sample"]!.AsArray());
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task ReadPermissionOnlyIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, ShopifyPermissions.ConnectionRead);

        var response = await client.GetAsync(SetupRoute(world), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task AnotherTenantsConnectionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var otherTenant = Guid.CreateVersion7();
        using var client = CreateClient(
            factory, world.Tenant.OwnerUserId, otherTenant, ShopifyPermissions.ConnectionRead, ShopifyPermissions.ConnectionManage);

        var response = await client.GetAsync($"/api/v1/tenants/{otherTenant}/shopify/stores/{world.ConnectionId}/setup", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE connection_id = @c", ("c", world.ConnectionId)));
        world.Tenant.Seeder.Dispose();
    }
}
