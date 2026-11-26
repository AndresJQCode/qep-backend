using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Shopify.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.2 y §7, plan Tarea 10: <c>POST …/setup/company</c> de punta a punta contra Postgres. La empresa
/// se crea por el caso de uso de Companies (sus permisos, su validador y sus claves de error) y se asigna a la tienda.
/// </summary>
public sealed class ShopifySetupCompanyApiTests
{
    private const string CompanyManage = "companies.company.manage";

    private const string ProfileWithCop = """{"currencyCode":"COP","billingAddress":{"countryCode":"CO"}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string StoreRoute(ShopifyWorld world) => $"/api/v1/tenants/{world.TenantId}/shopify/stores/{world.ConnectionId}";

    private static HttpClient ClientOf(QepApiFactory factory, ShopifyWorld world, bool companyManage = true) =>
        companyManage
            ? CreateClient(
                factory, world.Tenant.OwnerUserId, world.TenantId, ShopifyPermissions.ConnectionRead, ShopifyPermissions.ConnectionManage, CompanyManage)
            : CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, ShopifyPermissions.ConnectionRead, ShopifyPermissions.ConnectionManage);

    private static async Task<long> StoreVersionAsync(HttpClient client, ShopifyWorld world) =>
        (await client.GetFromJsonAsync<JsonObject>(StoreRoute(world), Ct))!["version"]!.GetValue<long>();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, ShopifyWorld world, object body, long? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{StoreRoute(world)}/setup/company") { Content = JsonContent.Create(body) };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<ShopifyWorld> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await ExecuteAsync(
            connectionString,
            "UPDATE integrations.connections SET fields = fields || jsonb_build_object('shopProfile', @profile::text) WHERE id = @id",
            ("profile", ProfileWithCop),
            ("id", world.ConnectionId));
        return world;
    }

    private static async Task<object> NewCompanyAsync(ShopifyWorld world, bool withCity = true) => new
    {
        name = "Mi Tienda SAS",
        taxId = $"900.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-1",
        documentType = "NIT",
        country = "CO",
        cityId = withCity ? await AnyCityIdAsync(world.Tenant.Seeder) : (Guid?)null,
        address = "Cra 1 # 2-3",
        chargesVat = true,
        bankAccount = new { bankName = "Davivienda", accountNumber = "4567-89", currency = "COP" },
    };

    [Fact]
    public async Task PostCreatesTheCompanyWithTheAccountAndAssignsIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);
        var version = await StoreVersionAsync(client, world);

        var response = await PostAsync(client, world, await NewCompanyAsync(world), version);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var companyId = body["companyId"]!.GetValue<Guid>();
        Assert.Equal(version + 1, body["storeVersion"]!.GetValue<long>());
        Assert.Equal($"\"{version + 1}\"", response.Headers.ETag!.Tag);
        Assert.Null(body["created"]);
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM companies.companies WHERE id = @id AND tenant_id = @t", ("id", companyId), ("t", world.TenantId)));
        var store = (await client.GetFromJsonAsync<JsonObject>(StoreRoute(world), Ct))!;
        Assert.Equal(companyId, store["billingCompanyId"]!.GetValue<Guid>());
        Assert.Equal(("Davivienda", "COP"), (store["billingBankName"]!.GetValue<string>(), store["billingCurrency"]!.GetValue<string>()));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task PostWithoutIfMatchIs428()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);

        var response = await PostAsync(client, world, new { companyId = world.Company.Id }, version: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task ACompaniesValidationErrorComesBackWithItsKeys()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);
        var version = await StoreVersionAsync(client, world);

        // En Colombia la ciudad es obligatoria: la regla y su clave son de Companies, no del validador del asistente.
        var response = await PostAsync(client, world, await NewCompanyAsync(world, withCity: false), version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal("validation.failed", problem["code"]!.GetValue<string>());
        Assert.NotNull(problem["errors"]!["CityId"]);
        Assert.Equal(version, await StoreVersionAsync(client, world));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task WithoutCompanyManageTheCreationIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world, companyManage: false);
        var version = await StoreVersionAsync(client, world);

        var response = await PostAsync(client, world, await NewCompanyAsync(world), version);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM companies.companies WHERE tenant_id = @t", ("t", world.TenantId)));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task AfterAssigningTheCompanyStepIsDone()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);
        var version = await StoreVersionAsync(client, world);

        var assigned = await PostAsync(client, world, new { companyId = world.Company.Id }, version);

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var setup = (await client.GetFromJsonAsync<JsonObject>($"{StoreRoute(world)}/setup", Ct))!;
        var steps = setup["steps"]!.AsArray().Select(step => step!.AsObject()).ToDictionary(step => step["key"]!.GetValue<string>());
        Assert.Equal("Done", steps["company"]["status"]!.GetValue<string>());
        Assert.Equal("Pending", steps["advisor"]["status"]!.GetValue<string>());
        Assert.Equal(world.Company.Id, setup["company"]!["selected"]!["id"]!.GetValue<Guid>());
        world.Tenant.Seeder.Dispose();
    }
}
