using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Catalog.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// La venta congela el costo unitario de cada línea (spec 2026-11-10 §4.2) y ninguna respuesta del
/// POS lo expone (§6.2): la columna se lee por SQL, porque la API no la entrega.
/// </summary>
public sealed class PosSaleCostApiTests
{
    private static readonly string[] CostManager = [.. SeedPermissions, CatalogPermissions.ProductCostManage];

    private sealed record CostedProduct(Guid ProductId, Guid VariantId, string Code);

    // El producto se crea por la API de catálogo con costo: es el mismo camino que usará el tenant.
    private static async Task<CostedProduct> CreateCostedProductAsync(
        HttpClient manager, Guid tenantId, string code, decimal? costCop)
    {
        var response = await manager.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = $"Producto {code}",
                variants = new[]
                {
                    new
                    {
                        code,
                        pricing = new
                        {
                            prices = new Dictionary<string, decimal> { ["COP"] = 11_900m },
                            scales = Array.Empty<object>(),
                            packagingUnits = Array.Empty<int>(),
                        },
                        costs = costCop is { } cost ? new Dictionary<string, decimal> { ["COP"] = cost } : null,
                    },
                },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadProductAsync(manager, tenantId, response, code);
    }

    private static async Task<CostedProduct> ReadProductAsync(
        HttpClient manager, Guid tenantId, HttpResponseMessage created, string code)
    {
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var productId = json.RootElement.GetProperty("id").GetGuid();
        var product = await GetProductAsync(manager, tenantId, productId);
        return new CostedProduct(productId, product.GetProperty("variants")[0].GetProperty("id").GetGuid(), code);
    }

    private static async Task<JsonElement> GetProductAsync(HttpClient manager, Guid tenantId, Guid productId)
    {
        var text = await manager.GetStringAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}", TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task ChangeCostAsync(HttpClient manager, Guid tenantId, CostedProduct product, decimal costCop)
    {
        var current = await GetProductAsync(manager, tenantId, product.ProductId);
        var response = await manager.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{product.ProductId}",
            new
            {
                expectedVersion = current.GetProperty("version").GetInt64(),
                name = current.GetProperty("name").GetString(),
                variants = new[]
                {
                    new
                    {
                        id = product.VariantId,
                        code = product.Code,
                        pricing = new
                        {
                            prices = new Dictionary<string, decimal> { ["COP"] = 11_900m },
                            scales = Array.Empty<object>(),
                            packagingUnits = Array.Empty<int>(),
                        },
                        costs = new Dictionary<string, decimal> { ["COP"] = costCop },
                    },
                },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static object[] LineOf(CostedProduct product, decimal quantity = 2m) =>
    [
        new { variantId = product.VariantId, quantity, discountPercentage = 0m, expectedUnitPrice = 11_900m, expectedTaxPercentage = 0 },
    ];

    private static async Task<(Guid SaleId, string Body)> SellAsync(PosWorld world, CostedProduct product)
    {
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        var response = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, LineOf(product), PosWorld.CashPayment(30_000m)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (id, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(decimal? UnitCost, string? Source, DateTimeOffset? CapturedAt)> FrozenCostAsync(
        Testcontainers.PostgreSql.PostgreSqlContainer database, Guid saleId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT unit_cost, cost_source, cost_captured_at FROM pos.sale_lines WHERE sale_id = @id", connection);
        command.Parameters.AddWithValue("id", saleId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (
            reader.IsDBNull(0) ? null : reader.GetDecimal(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    [Fact]
    public async Task ASaleFreezesTheCatalogCost()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        using var manager = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, CostManager);
        var product = await CreateCostedProductAsync(manager, world.Tenant.TenantId, "CS-01", 6_000m);

        var (saleId, _) = await SellAsync(world, product);

        var frozen = await FrozenCostAsync(database, saleId);
        Assert.Equal(6_000m, frozen.UnitCost);
        Assert.Equal("Manual", frozen.Source);
        Assert.NotNull(frozen.CapturedAt);
    }

    [Fact]
    public async Task ChangingTheCatalogCostDoesNotMoveASale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        using var manager = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, CostManager);
        var product = await CreateCostedProductAsync(manager, world.Tenant.TenantId, "CS-01", 6_000m);
        var (saleId, _) = await SellAsync(world, product);

        await ChangeCostAsync(manager, world.Tenant.TenantId, product, 9_000m);

        Assert.Equal(6_000m, (await FrozenCostAsync(database, saleId)).UnitCost);
    }

    [Fact]
    public async Task AVariantWithoutCostIsFrozenAsUncosted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        using var manager = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, CostManager);
        var product = await CreateCostedProductAsync(manager, world.Tenant.TenantId, "CS-02", null);

        var (saleId, _) = await SellAsync(world, product);

        var frozen = await FrozenCostAsync(database, saleId);
        Assert.Null(frozen.UnitCost);
        Assert.Equal("Manual", frozen.Source);
        Assert.NotNull(frozen.CapturedAt);
    }

    [Fact]
    public async Task NeitherTheSaleNorTheSearchCarryCosts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        using var manager = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, CostManager);
        var product = await CreateCostedProductAsync(manager, world.Tenant.TenantId, "CS-01", 6_000m);
        var (saleId, created) = await SellAsync(world, product);

        var read = await world.Admin.GetStringAsync($"{world.Url}/sales/{saleId}", TestContext.Current.CancellationToken);
        var search = await world.Admin.GetStringAsync($"{world.Url}/products?search=CS-01", TestContext.Current.CancellationToken);

        Assert.Contains("CS-01", search, StringComparison.Ordinal);
        Assert.DoesNotContain("cost", created, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost", read, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost", search, StringComparison.OrdinalIgnoreCase);
    }
}
