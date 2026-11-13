using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El costo congelado de las líneas del pedido por HTTP (spec costo de producto 2026-11-10 §4.3–§4.4): se toma del
/// catálogo cuando el pedido queda pagado, no se reescribe y nunca sale en una respuesta. El costo se siembra por SQL
/// en <c>catalog.product_variant_costs</c>: lo que se prueba es el pedido, no el API de catálogo.
/// </summary>
public sealed class OrderLineCostApiTests
{
    private static readonly DateTimeOffset Now = NewYearsEveInBogota;

    private static string OrderByIdUrl(Guid tenantId, Guid orderId) => $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    [Fact]
    public async Task MarkPaidFreezesTheCatalogCost()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, utcNow: Now);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (orderId, variantId) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        await SetCostAsync(connectionString, variantId, 6000m);

        Assert.Equal("<null>", await TextAsync(connectionString, $"SELECT unit_cost::text FROM quotations.order_items WHERE order_id = '{orderId}'"));

        await PostActionAsync(client, tenantId, orderId, "mark-paid");

        Assert.Equal("6000.00|Manual", await LineCostAsync(connectionString, orderId, variantId));
        Assert.Equal(Now, await PaidAtAsync(connectionString, orderId));
    }

    [Fact]
    public async Task UnmarkKeepsTheCostAndRemarkMovesPaidAt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, utcNow: Now);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (orderId, variantId) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        await SetCostAsync(connectionString, variantId, 6000m);
        await PostActionAsync(client, tenantId, orderId, "mark-paid");

        await PostActionAsync(client, tenantId, orderId, "unmark-paid");

        Assert.Null(await PaidAtAsync(connectionString, orderId));
        Assert.Equal("6000.00|Manual", await LineCostAsync(connectionString, orderId, variantId));

        await SetCostAsync(connectionString, variantId, 9000m);
        await PostActionAsync(client, tenantId, orderId, "mark-paid");

        Assert.Equal("6000.00|Manual", await LineCostAsync(connectionString, orderId, variantId));
        Assert.Equal(Now, await PaidAtAsync(connectionString, orderId));
    }

    [Fact]
    public async Task AnItemAddedAfterPaidFreezesOnArrival()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, utcNow: Now);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (orderId, _) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        await PostActionAsync(client, tenantId, orderId, "mark-paid");
        var addedId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 20_000m);
        await SetCostAsync(connectionString, addedId, 500m);

        var response = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, orderId)}/items",
            new AddOrderItemsRequest([new OrderItemAdditionRequest(addedId, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("500.00|Manual", await LineCostAsync(connectionString, orderId, addedId));
    }

    // Revisión de la tarea 5: el cálculo previo comparte OrderItemEdits con el guardado. Sin buscar el costo de la
    // línea nueva, agregar a un pedido pagado lanzaba en el dominio y respondía 500.
    [Fact]
    public async Task PreviewingAnEditThatAddsALineToAPaidOrderAnswersOk()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, utcNow: Now);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (orderId, variantId) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        await PostActionAsync(client, tenantId, orderId, "mark-paid");
        var addedId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 20_000m);
        await SetCostAsync(connectionString, addedId, 500m);

        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, orderId)}/preview",
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(variantId, 1m), new OrderEditItemRequest(addedId, 1m)], null, null),
            TestContext.Current.CancellationToken);

        Assert.True(
            preview.StatusCode == HttpStatusCode.OK,
            $"{(int)preview.StatusCode} {await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        // El cálculo previo no escribe: la línea no existe en la base.
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, $"SELECT count(*) FROM quotations.order_items WHERE order_id = '{orderId}'"));
    }

    [Fact]
    public async Task TheOrderResponsesNeverCarryCosts()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, utcNow: Now);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (orderId, variantId) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        await SetCostAsync(connectionString, variantId, 6000m);

        var markPaid = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, orderId)}/mark-paid", content: null, TestContext.Current.CancellationToken);
        var markPaidBody = await markPaid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var detailBody = await client.GetStringAsync(OrderByIdUrl(tenantId, orderId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, markPaid.StatusCode);
        Assert.Equal("6000.00|Manual", await LineCostAsync(connectionString, orderId, variantId));
        foreach (var body in new[] { markPaidBody, detailBody })
        {
            foreach (var field in new[] { "unitCost", "costSource", "costCapturedAt" })
            {
                Assert.DoesNotContain(field, body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // Revisión de la tarea 4: la tabla de costos no lleva tenant y el adaptador filtra por la variante. Y la moneda
    // del pedido puede no venir en mayúsculas: el adaptador la normaliza.
    [Fact]
    public async Task TheCostLookupIgnoresAVariantOfAnotherTenantAndNormalizesTheCurrency()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantA, _, clientA) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var a = clientA;
        var (tenantB, _, clientB) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var b = clientB;
        var variantB = await CreateProductWithScalesAsync(clientB, tenantB);
        await SetCostAsync(connectionString, variantB, 6000m);

        await using var scope = factory.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IOrderVariantCostLookup>();
        var fromA = await lookup.FindAsync(tenantA, "COP", [variantB], TestContext.Current.CancellationToken);
        var fromB = await lookup.FindAsync(tenantB, " cop ", [variantB], TestContext.Current.CancellationToken);
        var unsupported = await lookup.FindAsync(tenantB, "XXX", [variantB], TestContext.Current.CancellationToken);

        Assert.Empty(fromA);
        Assert.Equal(6000m, Assert.Single(fromB).Value);
        Assert.Empty(unsupported);
    }

    // Igual que OrderStatusActionsApiTests: convertida con un comprobante que cubre el neto, lista para marcar pagado.
    private static async Task<(Guid OrderId, Guid VariantId)> CreateOrderCoveredByAProofAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, variantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest(null, [new OrderPaymentProofRequest(fileId, quotation.NetTotal)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return (order.Id, variantId);
    }

    private static async Task PostActionAsync(HttpClient client, Guid tenantId, Guid orderId, string action)
    {
        var response = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, orderId)}/{action}", content: null, TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }

    internal static Task SetCostAsync(string connectionString, Guid variantId, decimal amount) =>
        ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO catalog.product_variant_costs (variant_id, currency, amount)
            VALUES ('{variantId}', 'COP', {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)})
            ON CONFLICT (variant_id, currency) DO UPDATE SET amount = EXCLUDED.amount
            """);

    internal static Task<string> LineCostAsync(string connectionString, Guid orderId, Guid variantId) =>
        TextAsync(
            connectionString,
            $"SELECT coalesce(unit_cost::text, '<null>') || '|' || coalesce(cost_source, '<null>') " +
            $"FROM quotations.order_items WHERE order_id = '{orderId}' AND variant_id = '{variantId}'");

    internal static async Task<DateTimeOffset?> PaidAtAsync(string connectionString, Guid orderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT paid_at FROM quotations.orders WHERE id = '{orderId}'", connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is DateTime instant ? new DateTimeOffset(instant, TimeSpan.Zero) : null;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? "<null>" : (string)value;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
