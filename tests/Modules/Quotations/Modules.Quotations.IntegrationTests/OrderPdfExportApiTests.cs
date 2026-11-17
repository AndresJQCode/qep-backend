using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// «Descargar PDF» del pedido (spec 2026-11-16): <c>POST /orders/{orderId}/pdf</c> devuelve el enlace
/// firmado, con <c>quotations.order.read</c> y en cualquier estado del pedido.
/// </summary>
public sealed class OrderPdfExportApiTests(SharedQuotationsHost sharedHost)
    : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    [Fact]
    public async Task ExportReturnsASignedUrlForTheOrder()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            OrderPdfUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = await response.Content.ReadFromJsonAsync<OrderPdfExportDto>(
            TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(export!.Url));
        Assert.NotNull(factory.PdfRenderer.LastDocument);
    }

    // Descargar es leer: el permiso es el de leer pedidos, no el de gestionarlos.
    [Fact]
    public async Task WithoutOrderReadItIsForbidden()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions.Where(permission => permission != OrdersPermissions.OrderRead)]);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            OrderPdfUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownOrderIsNotFound()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            OrderPdfUrl(tenantId, Guid.CreateVersion7()), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string OrderPdfUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}/pdf";

    private static async Task<OrderResponse> CreateOrderAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        return await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
    }
}
