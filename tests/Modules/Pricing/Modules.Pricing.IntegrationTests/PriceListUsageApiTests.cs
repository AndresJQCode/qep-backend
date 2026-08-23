using System.Net;
using System.Net.Http.Json;
using static Modules.Pricing.IntegrationTests.PricingApiHarness;

namespace Modules.Pricing.IntegrationTests;

/// <summary>
/// <c>DeletePriceListHandler</c> pregunta a Catalog y a Customers antes de borrar
/// (<c>IPriceListUsageLookup</c>, adaptado en Bootstrapper) — ninguno de los tres módulos se
/// referencia entre sí, así que estas pruebas son las únicas que ejercitan ese cableado de punta
/// a punta contra la app completa.
/// </summary>
public sealed class PriceListUsageApiTests
{
    [Fact]
    public async Task DeletingAPriceListReferencedByAProductPriceScaleReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var pricingClient = CreateManager(factory);
        using var catalogClient = CreateClient(
            factory, SubjectId, TenantId,
            "catalog.product.read", "catalog.product.manage");
        var priceList = await CreatePriceListAsync(pricingClient, "Mayorista", "MAY");

        var productResponse = await catalogClient.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name = "Camiseta",
                code = "CAM-001",
                pricing = new
                {
                    baseUsd = 10m,
                    finalUsd = 10m,
                    scales = new[]
                    {
                        new
                        {
                            priceListId = priceList.Id,
                            fromUnit = 1,
                            toUnit = 9,
                            discount = 0m,
                            restriction = "multiple",
                            multiple = 1,
                            finalUsd = 10m
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);

        var deleteResponse = await pricingClient.DeleteAsync(
            $"{PriceListsUrl()}/{priceList.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteResponse.StatusCode);
        var body = await deleteResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("pricing.price_list.in_use", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingAPriceListAssignedToACustomerReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var pricingClient = CreateManager(factory);
        using var customersClient = CreateClient(
            factory, SubjectId, TenantId,
            "customers.customer.read", "customers.customer.manage",
            "customers.classification.read", "customers.classification.manage");
        var priceList = await CreatePriceListAsync(pricingClient, "Mayorista", "MAY");

        var departments = await customersClient.GetFromJsonAsync<List<DepartmentDto>>(
            "/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        var cities = await customersClient.GetFromJsonAsync<List<CityDto>>(
            $"/api/v1/cities?departmentId={departments[0].Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(cities);
        Assert.NotEmpty(cities);

        var classificationResponse = await customersClient.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/customers/classifications",
            new { name = "Mediano", prefix = "MED" },
            TestContext.Current.CancellationToken);
        var classification = await classificationResponse.Content
            .ReadFromJsonAsync<ClassificationDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(classification);

        var customerResponse = await customersClient.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/customers",
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                cityId = cities[0].Id,
                classificationId = classification.Id,
                withRetention = false
            },
            TestContext.Current.CancellationToken);
        var customer = await customerResponse.Content
            .ReadFromJsonAsync<CustomerDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(customer);

        var assignResponse = await customersClient.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/customers/{customer.Id}/price-lists",
            new { priceListIds = new[] { priceList.Id } },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);

        var deleteResponse = await pricingClient.DeleteAsync(
            $"{PriceListsUrl()}/{priceList.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteResponse.StatusCode);
        var body = await deleteResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("pricing.price_list.in_use", body, StringComparison.Ordinal);
    }

    private sealed record DepartmentDto(Guid Id, string DivipolaCode, string Name);

    private sealed record CityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

    private sealed record ClassificationDto(Guid Id, string Name, string Prefix);

    private sealed record CustomerDto(Guid Id);
}
