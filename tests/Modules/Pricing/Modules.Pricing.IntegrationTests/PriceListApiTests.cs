using System.Net;
using System.Net.Http.Json;
using static Modules.Pricing.IntegrationTests.PricingApiHarness;

namespace Modules.Pricing.IntegrationTests;

/// <summary>
/// El catalogo de listas de precio (nombre + prefijo), mismo shape que ClientClassification en
/// Customers y TaxRate en Catalog: catalogo de referencia chico, tenant-scoped, con nombre y
/// prefijo unicos por tenant y estado activo/inactivo reversible.
/// </summary>
public sealed class PriceListApiTests
{
    [Fact]
    public async Task ListReturnsAnEmptyCatalogForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var body = await ListAsync(client);

        Assert.Empty(body.Items);
    }

    [Fact]
    public async Task ListReturnsOnlyThePriceListsOfTheAuthenticatedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateManager(factory);
        using var other = CreateManager(factory, OtherTenantId);

        await CreatePriceListAsync(owner, "Mayorista", "MAY");
        await CreatePriceListAsync(other, "Ajena", "AJE", OtherTenantId);

        var body = await ListAsync(owner);

        var single = Assert.Single(body.Items);
        Assert.Equal("Mayorista", single.Name);
        Assert.Equal("MAY", single.Prefix);
    }

    [Fact]
    public async Task CreateReturnsCreatedAndThePriceListIsReadableAfterwards()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            PriceListsUrl(),
            new { name = "Mayorista", prefix = "MAY" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Application.PriceListResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.True(created.IsActive);
        Assert.Equal("Mayorista", created.Name);
        Assert.Equal("MAY", created.Prefix);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.Equal($"{PriceListsUrl()}/{created.Id}", response.Headers.Location?.ToString());

        var fetched = await client.GetAsync(
            $"{PriceListsUrl()}/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task CreateWithOnlyTheReadPermissionIsForbiddenAndPersistsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var reader = CreateClient(
            factory, SubjectId, TenantId, Application.PricingPermissions.PriceListRead);

        var response = await reader.PostAsJsonAsync(
            PriceListsUrl(),
            new { name = "Mayorista", prefix = "MAY" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await ListAsync(reader)).Items);
    }

    [Fact]
    public async Task CreateWithBlankNameOrPrefixReturnsThePerFieldErrorMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.PostAsJsonAsync(
            PriceListsUrl(),
            new { name = "   ", prefix = "   " },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = await ValidationFieldsAsync(response);
        Assert.Contains("Name", fields);
        Assert.Contains("Prefix", fields);
    }

    [Fact]
    public async Task CreatingTheSameNameTwiceInATenantReturnsNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreatePriceListAsync(client, "Mayorista", "MAY");

        var second = await client.PostAsJsonAsync(
            PriceListsUrl(),
            new { name = "Mayorista", prefix = "OTR" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("pricing.price_list.name_taken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingTheSamePrefixTwiceInATenantReturnsPrefixTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        await CreatePriceListAsync(client, "Mayorista", "MAY");

        var second = await client.PostAsJsonAsync(
            PriceListsUrl(),
            new { name = "Minorista", prefix = "MAY" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("pricing.price_list.prefix_taken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateChangesNameAndPrefix()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreatePriceListAsync(client, "Mayorista", "MAY");

        var response = await client.PatchAsJsonAsync(
            $"{PriceListsUrl()}/{created.Id}",
            new { name = "Minorista", prefix = "MIN" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<Application.PriceListResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("Minorista", updated.Name);
        Assert.Equal("MIN", updated.Prefix);
    }

    [Fact]
    public async Task DeactivateThenActivateRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreatePriceListAsync(client, "Mayorista", "MAY");

        var deactivated = await client.PostAsync(
            $"{PriceListsUrl()}/{created.Id}/deactivate",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        var deactivatedBody = await deactivated.Content
            .ReadFromJsonAsync<Application.PriceListResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(deactivatedBody);
        Assert.False(deactivatedBody.IsActive);

        var activated = await client.PostAsync(
            $"{PriceListsUrl()}/{created.Id}/activate",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var activatedBody = await activated.Content
            .ReadFromJsonAsync<Application.PriceListResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(activatedBody);
        Assert.True(activatedBody.IsActive);
    }

    [Fact]
    public async Task DeactivatingAnAlreadyInactivePriceListReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreatePriceListAsync(client, "Mayorista", "MAY");
        await client.PostAsync(
            $"{PriceListsUrl()}/{created.Id}/deactivate",
            null,
            TestContext.Current.CancellationToken);

        var second = await client.PostAsync(
            $"{PriceListsUrl()}/{created.Id}/deactivate",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task DeleteRemovesAnUnusedPriceList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);
        var created = await CreatePriceListAsync(client, "Mayorista", "MAY");

        var response = await client.DeleteAsync(
            $"{PriceListsUrl()}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await ListAsync(client)).Items);
    }

    [Fact]
    public async Task GetForAnUnknownIdReturnsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateManager(factory);

        var response = await client.GetAsync(
            $"{PriceListsUrl()}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Una lista de otro tenant es inalcanzable por id: 403 y no 404, para no confirmar que el id
    // existe en otro tenant. Mismo criterio que ClientClassification/TaxRate.
    [Fact]
    public async Task GetAPriceListFromAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateManager(factory);
        using var other = CreateManager(factory, OtherTenantId);
        var created = await CreatePriceListAsync(owner, "Mayorista", "MAY");

        // La URL lleva el tenant del dueño, pero el cliente autentica como OtherTenantId: el
        // handler revalida que coincidan antes de tocar el repositorio, y por eso el resultado es
        // 403 y no 404 (que confirmaría que el id existe en otro tenant).
        var response = await other.GetAsync(
            $"{PriceListsUrl(TenantId)}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
