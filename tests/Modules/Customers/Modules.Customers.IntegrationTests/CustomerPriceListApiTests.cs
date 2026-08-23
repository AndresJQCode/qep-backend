using System.Net;
using System.Net.Http.Json;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// La relación N:N entre <c>Customer</c> y las listas de precio del módulo <c>pricing</c>
/// (<c>CustomerPriceList</c>). Sub-recurso propio, mismo criterio que
/// <c>ClientClassificationApiTests</c> frente a <c>CustomerApiTests</c>: <c>GET</c> resuelto y
/// <c>PUT</c> de reemplazo total, no altas/bajas granulares — igual que el <c>PUT</c> de escalas
/// de producto en Catalog.
/// </summary>
public sealed class CustomerPriceListApiTests
{
    private static string PriceListsUrl(string tenantId, Guid customerId) =>
        $"/api/v1/tenants/{tenantId}/customers/{customerId}/price-lists";

    private static string PricingPriceListsUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/pricing/price-lists";

    private static HttpClient CreateManager(QepApiFactory factory, string tenantId = TenantId) =>
        CreateClient(
            factory,
            SubjectId,
            tenantId,
            "customers.customer.read", "customers.customer.manage",
            "customers.classification.read", "customers.classification.manage",
            "pricing.price_list.read", "pricing.price_list.manage");

    // Nombres/prefijos deliberadamente distintos de los cinco que trae DefaultPriceListsSeeder
    // (Minorista/Mayorista/Distribuidor/Institucional/VIP): CustomersApiHarness.TenantId
    // coincide con TenancyDatabaseInitializer.DevelopmentTenantId, que se auto-provisiona y se
    // siembra apenas arranca la app en Development, así que "Mayorista"/"MAY" ya existe antes
    // de que esta prueba cree nada.
    private static async Task<Guid> CreatePriceListIdAsync(
        HttpClient client, string name = "Especial", string prefix = "ESP", string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            PricingPriceListsUrl(tenantId),
            new { name, prefix },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"No se pudo crear la lista de precios de prueba: {(int)response.StatusCode} " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<PriceListDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private static async Task<Guid> CreateCustomerIdAsync(HttpClient client)
    {
        var city = await EnsureCityAsync(client);
        var classification = await CreateClassificationAsync(client);
        var customer = await CreateCustomerAsync(client, city.CityId, classification.Id);
        return customer.Id;
    }

    [Fact]
    public async Task ListReturnsAnEmptySetForACustomerWithNoAssignments()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(client);

        var response = await client.GetFromJsonAsync<PriceListsResponse>(
            PriceListsUrl(TenantId, customerId), TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task SetAssignsASinglePriceList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(client);
        var priceListId = await CreatePriceListIdAsync(client);

        var response = await client.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = new[] { priceListId } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PriceListsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        var single = Assert.Single(body.Items);
        Assert.Equal(priceListId, single.Id);
        Assert.Equal("Especial", single.Name);

        // Confirmado con un GET aparte, no sólo con la respuesta del PUT.
        var fetched = await client.GetFromJsonAsync<PriceListsResponse>(
            PriceListsUrl(TenantId, customerId), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Single(fetched.Items);
    }

    [Fact]
    public async Task SetAssignsMultiplePriceLists()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(client);
        var wholesale = await CreatePriceListIdAsync(client, "Especial", "ESP");
        var vip = await CreatePriceListIdAsync(client, "Premium", "PRE");

        var response = await client.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = new[] { wholesale, vip } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PriceListsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(2, body.Items.Count);
    }

    // El body es un conjunto: repetir el mismo id no crea una segunda fila.
    [Fact]
    public async Task SetWithADuplicateIdInTheBodyDoesNotCreateTwoAssignments()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(client);
        var priceListId = await CreatePriceListIdAsync(client);

        var response = await client.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = new[] { priceListId, priceListId } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PriceListsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Single(body.Items);
    }

    // PUT reemplaza el conjunto entero: mandar sólo una de las dos que ya estaban asignadas deja
    // sin asignar la que no vino, mismo criterio que las escalas de producto.
    [Fact]
    public async Task SetWithAnEmptyArrayRemovesAllAssignments()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(client);
        var priceListId = await CreatePriceListIdAsync(client);
        await client.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = new[] { priceListId } },
            TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PriceListsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Empty(body.Items);
    }

    [Fact]
    public async Task SetWithAnUnknownPriceListIdReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(client);

        var response = await client.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = new[] { Guid.NewGuid() } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "customers.customer.price_list_not_found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetWithAnInactivePriceListIdReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(client);
        var priceListId = await CreatePriceListIdAsync(client);
        await client.PostAsync(
            $"{PricingPriceListsUrl()}/{priceListId}/deactivate",
            null,
            TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = new[] { priceListId } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "customers.customer.price_list_inactive", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetWithOnlyTheReadPermissionIsForbiddenAndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateManager(factory);
        var customerId = await CreateCustomerIdAsync(manager);
        var priceListId = await CreatePriceListIdAsync(manager);
        using var reader = CreateClient(factory, SubjectId, TenantId, "customers.customer.read");

        var response = await reader.PutAsJsonAsync(
            PriceListsUrl(TenantId, customerId),
            new { priceListIds = new[] { priceListId } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var fetched = await manager.GetFromJsonAsync<PriceListsResponse>(
            PriceListsUrl(TenantId, customerId), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Empty(fetched.Items);
    }

    private sealed record PriceListDto(Guid Id, string Name, string Prefix, bool IsActive);

    private sealed record PriceListsResponse(IReadOnlyCollection<PriceListDto> Items);
}
