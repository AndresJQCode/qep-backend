using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// A pedido (2026-09-15): "Editar" un pedido pendiente también admite corregir la cantidad de un
/// producto, o quitarlo — reusando `PUT`/`DELETE /quotations/{id}/items/{itemId}`, el mismo
/// endpoint que <see cref="QuotationItemApiTests"/> ya cubre para Draft/Sent. Esta clase prueba
/// el caso nuevo: la cotización Converted con un pedido Pending.
/// </summary>
public sealed class OrderItemEditApiTests
{
    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    [Fact]
    public async Task UpdateItemQuantityOnAPendingOrderRecalculatesTheQuotationTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var itemId = Assert.Single(quotation.Items).Id;
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(2m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(2m, Assert.Single(updated.Items).Quantity);
        Assert.True(updated.Total > quotation.Total);
    }

    [Fact]
    public async Task UpdateItemQuantityOnAnApprovedOrderIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var itemId = Assert.Single(quotation.Items).Id;
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(2m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("order.order.not_pending", problem.Code);
    }

    [Fact]
    public async Task RemoveItemOnAPendingOrderRecalculatesTheQuotationTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstItemId = Assert.Single(quotation.Items).Id;
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var secondProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        (await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/items",
            new AddOrderItemsRequest([new OrderItemAdditionRequest(secondProductId, 1m)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.DeleteAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{firstItemId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        var remaining = Assert.Single(updated.Items);
        Assert.Equal(secondProductId, remaining.ProductId);
    }

    // Sin este chequeo, un pedido pendiente podia quedarse con una cotizacion en cero productos.
    [Fact]
    public async Task RemoveItemRejectsRemovingTheLastRemainingItemOnAPendingOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var itemId = Assert.Single(quotation.Items).Id;
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.DeleteAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.item.last_item_required", problem.Code);
    }

    [Fact]
    public async Task RemoveItemOnAnApprovedOrderIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var itemId = Assert.Single(quotation.Items).Id;
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.DeleteAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("order.order.not_pending", problem.Code);
    }

    private sealed record ProblemPayload(string Code);
}
