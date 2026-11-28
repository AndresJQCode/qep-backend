using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Los eventos de inventario del pedido salen al outbox en la misma transacción que el cambio: preparar escribe
/// <c>quotations.order.prepared.v1</c> con la versión que dejó la mutación, el origen <c>Manual</c> y las líneas del pedido; y
/// la anulación que llega de la plataforma (Shopify) marca <c>fromPlatform</c>, la de la consola no.
/// </summary>
public sealed class OrderStockEventsOutboxTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private static readonly string[] AllPermissions =
    [
        .. ApproverPermissions, OrdersPermissions.OrderInvoice, OrdersPermissions.OrderCancel,
    ];

    private static string ActionUrl(Guid tenantId, Guid orderId, string action) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}/{action}";

    private static async Task<OrderResponse> CreateOrderAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, variantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    private static async Task<OrderResponse> PostActionAsync(HttpClient client, Guid tenantId, Guid orderId, string action)
    {
        var response = await client.PostAsync(ActionUrl(tenantId, orderId, action), content: null, TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    [Fact]
    public async Task PrepareWritesItsEventInTheSameTransaction()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var prepared = await PostActionAsync(client, tenantId, order.Id, "prepare");

        var messages = await OutboxMessagesAsync(factory, "quotations.order.prepared.v1");
        var message = Assert.Single(messages, candidate =>
            JsonDocument.Parse(candidate.PayloadJson).RootElement.GetProperty("orderId").GetGuid() == order.Id);
        using var json = JsonDocument.Parse(message.PayloadJson);
        var payload = json.RootElement;
        Assert.Equal(tenantId, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(prepared.Version, payload.GetProperty("version").GetInt64());
        Assert.Equal("Manual", payload.GetProperty("origin").GetString());
        var line = Assert.Single(payload.GetProperty("lines").EnumerateArray());
        Assert.Equal("Physical", line.GetProperty("productKind").GetString());
        Assert.Equal(order.Id.ToString("D"), message.CorrelationId);
    }

    [Fact]
    public async Task AConsoleCancelIsNotFromThePlatform()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            ActionUrl(tenantId, order.Id, "cancel"), new { reason = "Sin stock" }, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var messages = await OutboxMessagesAsync(factory, "quotations.order.cancelled.v1");
        var message = Assert.Single(messages, candidate =>
            JsonDocument.Parse(candidate.PayloadJson).RootElement.GetProperty("orderId").GetGuid() == order.Id);
        using var json = JsonDocument.Parse(message.PayloadJson);
        Assert.False(json.RootElement.GetProperty("fromPlatform").GetBoolean());
        Assert.True(json.RootElement.GetProperty("version").GetInt64() > 1);
    }
}
