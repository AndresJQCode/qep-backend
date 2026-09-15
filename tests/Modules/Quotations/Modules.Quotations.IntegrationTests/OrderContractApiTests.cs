using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Reporting.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El contrato de pedidos tal como viaja (spec 2026-09-14): rutas, nombres de campo y el header
/// Location. Lee el JSON crudo a propósito: el resto de las pruebas deserializa con los mismos
/// records de producción, así que renombrar una propiedad las deja en verde aunque el frontend deje
/// de encontrar el campo.
/// </summary>
public sealed class OrderContractApiTests
{
    [Fact]
    public async Task TheOrderTravelsWithItsNewRoutesAndFieldNames()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, ReportingPermissions.OrdersRead]);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        using (var before = await GetJsonAsync(client, $"{QuotationsUrl(tenantId)}/{quotation.Id}"))
        {
            Assert.True(before.RootElement.GetProperty("canBeConvertedToOrder").GetBoolean());
            Assert.False(before.RootElement.TryGetProperty("canBeConvertedToSale", out var ignoredCanBeConvertedToSale));
        }

        var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, converted.StatusCode);
        Assert.Equal(
            $"/api/v1/tenants/{tenantId}/quotations/{quotation.Id}/order",
            converted.Headers.Location?.OriginalString);
        using var created = JsonDocument.Parse(
            await converted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var orderId = created.RootElement.GetProperty("id").GetGuid();
        var orderNumber = created.RootElement.GetProperty("orderNumber").GetString();
        Assert.False(created.RootElement.TryGetProperty("saleNumber", out var ignoredSaleNumber));

        using (var byQuotation = await GetJsonAsync(client, $"{QuotationsUrl(tenantId)}/{quotation.Id}/order"))
        {
            Assert.Equal(orderNumber, byQuotation.RootElement.GetProperty("orderNumber").GetString());
        }

        using (var list = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/orders?orderNumber={orderNumber}"))
        {
            var row = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(orderNumber, row.GetProperty("orderNumber").GetString());
        }

        using (var detail = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/orders/{orderId}"))
        {
            Assert.False(detail.RootElement.TryGetProperty("sale", out var ignoredSale));
            Assert.Equal(orderNumber, detail.RootElement.GetProperty("order").GetProperty("orderNumber").GetString());
            var composed = detail.RootElement.GetProperty("quotation");
            Assert.Equal(quotation.Id, composed.GetProperty("id").GetGuid());
            Assert.False(composed.GetProperty("canBeConvertedToOrder").GetBoolean());
        }

        using (var quotations = await GetJsonAsync(client, $"{QuotationsUrl(tenantId)}?status=Converted"))
        {
            var row = Assert.Single(quotations.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(orderId, row.GetProperty("orderId").GetGuid());
            Assert.Equal("Pending", row.GetProperty("orderStatus").GetString());
            Assert.False(row.TryGetProperty("saleId", out var ignoredSaleId));
            Assert.False(row.TryGetProperty("saleStatus", out var ignoredSaleStatus));
        }

        using (var reportList = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/reports/orders"))
        {
            var reportItem = Assert.Single(reportList.RootElement.GetProperty("items").EnumerateArray());
            Assert.False(reportItem.TryGetProperty("saleId", out var ignoredReportSaleId));
            Assert.False(reportItem.TryGetProperty("saleNumber", out var ignoredReportSaleNumber));
        }

        using (var reportSummary = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/reports/orders/summary"))
        {
            Assert.False(reportSummary.RootElement.TryGetProperty("saleCount", out var ignoredSaleCount));
        }
    }

    [Fact]
    public async Task ProofsAndApprovalHangFromTheOrderSubresource()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var proofs = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order/proofs",
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        var approve = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order/approve",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, proofs.StatusCode);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var approved = JsonDocument.Parse(
            await approve.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Approved", approved.RootElement.GetProperty("status").GetString());
    }

    // El export recibe el mismo filtro que el listado: con orderNumber sin coincidencias no hay
    // filas, y el 422 lleva el código del área de pedidos (antes sale.export.empty).
    [Fact]
    public async Task ExportFiltersByOrderNumberAndAnswersWithTheOrderCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/orders/export?convertedFrom={today.AddDays(-1):yyyy-MM-dd}&convertedTo={today:yyyy-MM-dd}&orderNumber=NO-EXISTE",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.export.empty", body, StringComparison.Ordinal);
    }

    // Corte duro (D3): ninguna ruta vieja queda como alias. Cubre las nueve rutas de `sale(s)` que
    // existían antes del rename -- listado, detalle, export, conversión, comprobantes, aprobación
    // y los dos reportes -- cada una esperando 404, no sólo las tres más obvias. Más
    // `sale/items`, que sólo llegó a existir en feature/sales y entró ya como `order/items`.
    [Fact]
    public async Task TheSalesRoutesNoLongerExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotationId = Guid.CreateVersion7();

        string[] getRoutes =
        [
            $"/api/v1/tenants/{tenantId}/sales",
            $"/api/v1/tenants/{tenantId}/sales/{Guid.CreateVersion7()}",
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale",
            $"/api/v1/tenants/{tenantId}/reports/sales",
            $"/api/v1/tenants/{tenantId}/reports/sales/summary",
        ];
        foreach (var url in getRoutes)
        {
            var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        string[] postRoutes =
        [
            $"/api/v1/tenants/{tenantId}/sales/export",
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale",
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale/approve",
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale/proofs",
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale/items",
        ];
        foreach (var url in postRoutes)
        {
            var response = await client.PostAsync(url, content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
