using System.Net.Http.Json;
using BuildingBlocks.Domain;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La limpieza de Quotations que corre el borrado de producto (spec 2026-11-08), con el adaptador real de
/// <c>Bootstrapper</c> contra el esquema migrado: congela código y nombre en las líneas que se quedan, saca las
/// líneas de los borradores y los revaloriza. Corre dentro de la transacción de Catalog, como la correrá el
/// handler del <c>DELETE</c>, y <see cref="NothingPersistsWhenTheCatalogTransactionIsNotCommitted"/> es la
/// prueba de que Quotations quedó enrolado en ella.
/// </summary>
public sealed class ProductDeletionCleanupTests
{
    private const string SettingsUpdate = "tenancy.settings.update";

    private static readonly object[] GroupableScales =
    [
        new { fromUnit = 1, toUnit = 9, discount = 0m, restriction = "multiple", multiple = 1 },
        new { fromUnit = 10, toUnit = 999, discount = 5m, restriction = "multiple", multiple = 1, allowGrouping = true }
    ];

    private static readonly object[] FivePercentScales =
    [
        new { fromUnit = 1, toUnit = 999, discount = 5m, restriction = "multiple", multiple = 1 }
    ];

    // Lo que hará el DELETE: abrir la transacción de Catalog, limpiar y confirmar (o no).
    private static async Task CleanAsync(
        QepApiFactory factory, Guid tenantId, IReadOnlyCollection<Guid> variantIds, bool commit = true)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>();
        var cleanup = scope.ServiceProvider.GetRequiredService<IProductDeletionCleanup>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await cleanup.ApplyAsync(tenantId, variantIds, DateTimeOffset.UtcNow, cancellationToken);

        if (commit)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<(string? Code, string? Name)> SnapshotOfAsync(
        string connectionString, string table, string ownerColumn, Guid ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT product_code, product_name FROM quotations.{table} WHERE {ownerColumn} = @owner", connection);
        command.Parameters.AddWithValue("owner", ownerId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static Task NullQuotationSnapshotAsync(string connectionString, Guid quotationId) =>
        ExecuteAsync(
            connectionString,
            "UPDATE quotations.quotation_items SET product_code = NULL, product_name = NULL WHERE quotation_id = @id",
            ("id", quotationId));

    private static Task NullOrderSnapshotAsync(string connectionString, Guid orderId) =>
        ExecuteAsync(
            connectionString,
            "UPDATE quotations.order_items SET product_code = NULL, product_name = NULL WHERE order_id = @id",
            ("id", orderId));

    private static async Task<OrderResponse> ConvertAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    private static async Task AddLineAsync(HttpClient client, Guid tenantId, Guid quotationId, Guid variantId, decimal quantity = 1m)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/items",
            new AddQuotationItemRequest(variantId, quantity),
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<QuotationResponse> GetQuotationAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var body = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}", TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<QuotationResponse> DraftWithAsync(
        HttpClient client, Guid tenantId, Guid customerId, params (Guid VariantId, decimal Quantity)[] lines)
    {
        var draft = await CreateQuotationAsync(client, tenantId, customerId);
        foreach (var (variantId, quantity) in lines)
        {
            await AddLineAsync(client, tenantId, draft.Id, variantId, quantity);
        }

        return await GetQuotationAsync(client, tenantId, draft.Id);
    }

    [Fact]
    public async Task SentAndOrderLinesKeepTheirCodeAndTrimmedName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var name = "Camiseta " + new string('x', 191);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId, name: name);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, shirt.M);
        var forOrder = await CreateSentQuotationAsync(client, factory, tenantId, customerId, shirt.M);
        var order = await ConvertAsync(client, tenantId, forOrder.Id);
        var connectionString = database.GetConnectionString();
        await NullQuotationSnapshotAsync(connectionString, sent.Id);
        await NullOrderSnapshotAsync(connectionString, order.Id);

        await CleanAsync(factory, tenantId, [shirt.M, shirt.L]);

        var expected = ("CAM-M", SnapshotText.Fit($"{name} – M", 200));
        // El nombre visible pasa de 200: la foto es la recortada, no la entera.
        Assert.NotEqual($"{name} – M", expected.Item2);
        Assert.Equal(expected, await SnapshotOfAsync(connectionString, "quotation_items", "quotation_id", sent.Id));
        Assert.Equal(expected, await SnapshotOfAsync(connectionString, "order_items", "order_id", order.Id));
    }

    [Fact]
    public async Task ADraftWhoseGroupedTierDropsIsRepriced()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);
        var productA = await CreateProductWithScalesAsync(client, tenantId, 100_000m, GroupableScales, vat);
        var productB = await CreateProductWithScalesAsync(client, tenantId, 80_000m, GroupableScales);
        var draft = await DraftWithAsync(client, tenantId, customerId, (productA, 6m), (productB, 6m));
        Assert.All(draft.Items, item => Assert.Equal(5m, item.DiscountPercentage));
        // Lo que el borrador valdría con A sola, sin descuento: 6 × el precio de A con su IVA.
        var aloneA = await DraftWithAsync(client, tenantId, customerId, (productA, 6m));

        await CleanAsync(factory, tenantId, [productB]);

        var repriced = await GetQuotationAsync(client, tenantId, draft.Id);
        var line = Assert.Single(repriced.Items);
        Assert.Equal(productA, line.VariantId);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.Equal(draft.Version + 1, repriced.Version);
        Assert.Equal(aloneA.Total, repriced.Total);
    }

    [Fact]
    public async Task ADraftThatFallsUnderTheMinimumPurchaseIsRepriced()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, [.. ManagerPermissions, SettingsUpdate]);
        using var _ = client;
        var settings = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/quotations/settings",
            new { minimumUnits = 10, minimumTotals = new Dictionary<string, decimal> { ["COP"] = 100_000_000m } },
            TestContext.Current.CancellationToken);
        settings.EnsureSuccessStatusCode();
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productA = await CreateProductWithScalesAsync(client, tenantId, 100_000m, FivePercentScales);
        var productB = await CreateProductWithScalesAsync(client, tenantId);
        var draft = await DraftWithAsync(client, tenantId, customerId, (productA, 5m), (productB, 6m));
        Assert.Equal(5m, draft.Items.Single(item => item.VariantId == productA).DiscountPercentage);

        await CleanAsync(factory, tenantId, [productB]);

        var repriced = await GetQuotationAsync(client, tenantId, draft.Id);
        var line = Assert.Single(repriced.Items);
        Assert.Equal((productA, 0m), (line.VariantId, line.DiscountPercentage));
    }

    [Fact]
    public async Task NothingPersistsWhenTheCatalogTransactionIsNotCommitted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var deleted = await CreateProductWithScalesAsync(client, tenantId);
        var kept = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, deleted);
        var draft = await DraftWithAsync(client, tenantId, customerId, (deleted, 1m), (kept, 1m));
        var connectionString = database.GetConnectionString();
        await NullQuotationSnapshotAsync(connectionString, sent.Id);

        await CleanAsync(factory, tenantId, [deleted], commit: false);

        Assert.Equal((null, null), await SnapshotOfAsync(connectionString, "quotation_items", "quotation_id", sent.Id));
        var untouched = await GetQuotationAsync(client, tenantId, draft.Id);
        Assert.Equal(2, untouched.Items.Count);
        Assert.Equal(draft.Version, untouched.Version);
    }

    [Fact]
    public async Task AnotherTenantIsUntouched()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = otherClient;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var deleted = await CreateProductWithScalesAsync(client, tenantId);
        var kept = await CreateProductWithScalesAsync(client, tenantId);
        var ownDraft = await DraftWithAsync(client, tenantId, customerId, (deleted, 1m), (kept, 1m));
        var otherCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);
        var otherProduct = await CreateProductWithScalesAsync(otherClient, otherTenantId);
        var otherSent = await CreateSentQuotationAsync(otherClient, factory, otherTenantId, otherCustomerId, otherProduct);
        var otherDraft = await DraftWithAsync(otherClient, otherTenantId, otherCustomerId, (otherProduct, 1m));
        var connectionString = database.GetConnectionString();
        // El otro tenant tiene líneas con la variante que borra `tenantId`: sólo se consigue por SQL.
        await ExecuteAsync(
            connectionString,
            "UPDATE quotations.quotation_items SET variant_id = @variant WHERE quotation_id = ANY(@ids)",
            ("variant", deleted),
            ("ids", new[] { otherSent.Id, otherDraft.Id }));
        await NullQuotationSnapshotAsync(connectionString, otherSent.Id);

        await CleanAsync(factory, tenantId, [deleted]);

        // El tenant que borra sí se limpió: la prueba no pasa por no hacer nada.
        Assert.Equal(kept, Assert.Single((await GetQuotationAsync(client, tenantId, ownDraft.Id)).Items).VariantId);
        Assert.Equal(
            (null, null),
            await SnapshotOfAsync(connectionString, "quotation_items", "quotation_id", otherSent.Id));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connectionString, "SELECT count(*) FROM quotations.quotation_items WHERE quotation_id = @id", otherDraft.Id));
        Assert.Equal(
            otherDraft.Version,
            await ScalarAsync<long>(connectionString, "SELECT version FROM quotations.quotations WHERE id = @id", otherDraft.Id));
    }

    [Fact]
    public async Task ApplyingOutsideACatalogTransactionThrows()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredService<IProductDeletionCleanup>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ApplyAsync(
            Guid.CreateVersion7(), [Guid.CreateVersion7()], DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }
}
