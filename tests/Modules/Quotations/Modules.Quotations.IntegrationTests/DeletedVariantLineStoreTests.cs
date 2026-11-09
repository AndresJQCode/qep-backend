using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>El SQL por nombre sobre las tablas de Quotations que corre el borrado de producto (spec 2026-11-08):
/// el congelado del código y el nombre en líneas sin foto, y la carga de los borradores afectados. Contra el
/// esquema migrado.</summary>
public sealed class DeletedVariantLineStoreTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static Dictionary<Guid, QuotationProductSnapshot> Snapshots(Guid variantId) =>
        new() { [variantId] = new QuotationProductSnapshot("NEW-CODE", "Nombre nuevo") };

    private static async Task<int> BackfillAsync(
        QepApiFactory factory, Guid tenantId, IReadOnlyDictionary<Guid, QuotationProductSnapshot> snapshots)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDeletedVariantLineStore>();
        return await store.BackfillProductSnapshotsAsync(tenantId, snapshots, TestContext.Current.CancellationToken);
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

    private static async Task AddLineAsync(HttpClient client, Guid tenantId, Guid quotationId, Guid variantId) =>
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/items",
            new AddQuotationItemRequest(variantId, 1m),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

    [Fact]
    public async Task BackfillFillsSentAndOrderLinesWithoutSnapshot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, variantId);
        var forOrder = await CreateSentQuotationAsync(client, factory, tenantId, customerId, variantId);
        var order = await ConvertAsync(client, tenantId, forOrder.Id);
        var connectionString = database.GetConnectionString();
        await NullQuotationSnapshotAsync(connectionString, sent.Id);
        await NullOrderSnapshotAsync(connectionString, order.Id);

        var touched = await BackfillAsync(factory, tenantId, Snapshots(variantId));

        Assert.Equal(2, touched);
        Assert.Equal(
            ("NEW-CODE", "Nombre nuevo"),
            await SnapshotOfAsync(connectionString, "quotation_items", "quotation_id", sent.Id));
        Assert.Equal(
            ("NEW-CODE", "Nombre nuevo"),
            await SnapshotOfAsync(connectionString, "order_items", "order_id", order.Id));
    }

    [Fact]
    public async Task BackfillLeavesDraftsAndExistingSnapshotsAlone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var draft = await CreateQuotationAsync(client, tenantId, customerId);
        await AddLineAsync(client, tenantId, draft.Id, variantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, variantId);
        var connectionString = database.GetConnectionString();
        await ExecuteAsync(
            connectionString,
            "UPDATE quotations.quotation_items SET product_code = 'OLD', product_name = 'OLD' WHERE quotation_id = @id",
            ("id", sent.Id));

        var touched = await BackfillAsync(factory, tenantId, Snapshots(variantId));

        Assert.Equal(0, touched);
        Assert.Equal(
            (null, null),
            await SnapshotOfAsync(connectionString, "quotation_items", "quotation_id", draft.Id));
        Assert.Equal(
            ("OLD", "OLD"),
            await SnapshotOfAsync(connectionString, "quotation_items", "quotation_id", sent.Id));
    }

    [Fact]
    public async Task BackfillNeverTouchesAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = otherClient;
        var otherCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);
        var otherVariantId = await CreateProductWithScalesAsync(otherClient, otherTenantId);
        var otherSent = await CreateSentQuotationAsync(
            otherClient, factory, otherTenantId, otherCustomerId, otherVariantId);
        var connectionString = database.GetConnectionString();
        await NullQuotationSnapshotAsync(connectionString, otherSent.Id);

        // El diccionario trae la variante del otro tenant, pero el tenant que borra es `tenantId`.
        var touched = await BackfillAsync(factory, tenantId, Snapshots(otherVariantId));

        Assert.Equal(0, touched);
        Assert.Equal(
            (null, null),
            await SnapshotOfAsync(connectionString, "quotation_items", "quotation_id", otherSent.Id));
    }

    [Fact]
    public async Task ListDraftsReturnsOnlyTrackedDraftsOfTheTenantWithThoseVariants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (otherTenantId, _, otherClient) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = otherClient;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var otherVariantId = await CreateProductWithScalesAsync(client, tenantId);
        var withVariant = await CreateQuotationAsync(client, tenantId, customerId);
        await AddLineAsync(client, tenantId, withVariant.Id, variantId);
        await AddLineAsync(client, tenantId, withVariant.Id, otherVariantId);
        await CreateSentQuotationAsync(client, factory, tenantId, customerId, variantId);
        var withoutVariant = await CreateQuotationAsync(client, tenantId, customerId);
        await AddLineAsync(client, tenantId, withoutVariant.Id, otherVariantId);
        var otherCustomerId = await CreateActiveCustomerAsync(otherClient, otherTenantId);
        var otherDraft = await CreateQuotationAsync(otherClient, otherTenantId, otherCustomerId);
        await AddLineAsync(
            otherClient, otherTenantId, otherDraft.Id, await CreateProductWithScalesAsync(otherClient, otherTenantId));

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDeletedVariantLineStore>();
        // El servicio de la Tarea 7 se registra en Bootstrapper con todas sus dependencias: tiene que resolver.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IQuotationVariantDeletion>());
        var drafts = await store.ListDraftsWithVariantsAsync(
            tenantId, [variantId], TestContext.Current.CancellationToken);

        var draft = Assert.Single(drafts);
        Assert.Equal(withVariant.Id, draft.Id.Value);
        Assert.Equal(2, draft.Items.Count);

        // Rastreado: quitar la línea y guardar con el unit of work la persiste.
        draft.RemoveItemsOfDeletedVariants(new HashSet<Guid> { variantId }, DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        var reloaded = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{withVariant.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.Equal(otherVariantId, Assert.Single(reloaded.Items).ProductId);
    }
}
