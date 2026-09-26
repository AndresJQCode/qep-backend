using System.Globalization;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// De punta a punta (owner, 2026-09-26): código y nombre del producto se leen en vivo del
/// catálogo mientras la cotización es borrador, y se congelan cuando sale del borrador —al
/// enviarla o al convertirla en pedido—. El cambio en Catalog se hace por su API, como lo haría
/// una persona, y no tocando la tabla: lo que se prueba es que Quotations deje de mirarla.
/// </summary>
public sealed class QuotationProductSnapshotApiTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    [Fact]
    public async Task ASentQuotationKeepsTheProductItWasSentWithWhileADraftFollowsTheCatalog()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var sentCode = Assert.Single(sent.Items).ProductCode;
        var draft = await CreateQuotationAsync(client, tenantId, customerId);
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await RenameProductAsync(client, tenantId, productId, "Vela renombrada", "VR-NUEVO");

        var sentAfter = await GetQuotationAsync(client, tenantId, sent.Id);
        var sentItem = Assert.Single(sentAfter.Items);
        Assert.Equal((sentCode, "Vela de soja"), (sentItem.ProductCode, sentItem.ProductName));
        var draftItem = Assert.Single((await GetQuotationAsync(client, tenantId, draft.Id)).Items);
        Assert.Equal(("VR-NUEVO", "Vela renombrada"), (draftItem.ProductCode, draftItem.ProductName));
    }

    // El pedido hereda lo congelado: su detalle y el Excel del ERP dicen el código con que se envió.
    [Fact]
    public async Task TheOrderAndItsWorkbookKeepTheCodeTheQuotationWasSentWith()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var sentCode = Assert.Single(sent.Items).ProductCode;
        var order = await ConvertAsync(client, tenantId, sent.Id);

        await RenameProductAsync(client, tenantId, productId, "Vela renombrada", "VR-NUEVO");

        var detail = await GetOrderDetailAsync(client, tenantId, order.Id);
        Assert.Equal(sentCode, Assert.Single(detail.Quotation.Items).ProductCode);
        var sheet = await ExportOrdersAsync(client, factory, tenantId);
        Assert.Equal("Cod. Producto", sheet.Rows[0][1]);
        Assert.Equal(sentCode, sheet.Rows[1][1]);
    }

    // Un borrador se puede convertir sin haberse enviado: la conversión también congela.
    [Fact]
    public async Task ConvertingADraftFreezesTheProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var draft = await CreateQuotationAsync(
            client, tenantId, customerId,
            billingAccount: new QuotationBillingAccountRequest(
                billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var draftCode = Assert.Single((await GetQuotationAsync(client, tenantId, draft.Id)).Items).ProductCode;
        var order = await ConvertAsync(client, tenantId, draft.Id);

        await RenameProductAsync(client, tenantId, productId, "Vela renombrada", "VR-NUEVO");

        var detail = await GetOrderDetailAsync(client, tenantId, order.Id);
        Assert.Equal(draftCode, Assert.Single(detail.Quotation.Items).ProductCode);
    }

    // Una línea que se le suma al pedido pendiente no tiene envío posterior: se congela al sumarla.
    [Fact]
    public async Task ALineAddedToAPendingOrderIsFrozenWhenAdded()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
        var order = await ConvertAsync(client, tenantId, sent.Id);
        var addedProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        (await client.PostAsJsonAsync(
            $"{OrdersUrl(tenantId)}/{order.Id}/items",
            new AddOrderItemsRequest([new OrderItemAdditionRequest(addedProductId, 1m)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var addedCode = (await GetOrderDetailAsync(client, tenantId, order.Id)).Quotation.Items
            .Single(item => item.ProductId == addedProductId).ProductCode;

        await RenameProductAsync(client, tenantId, addedProductId, "Otra renombrada", "OR-NUEVO", baseCop: 50_000m);

        var detail = await GetOrderDetailAsync(client, tenantId, order.Id);
        Assert.Equal(addedCode, detail.Quotation.Items.Single(item => item.ProductId == addedProductId).ProductCode);
    }

    private static async Task<QuotationResponse> GetQuotationAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var quotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}", TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<OrderDetailResponse> GetOrderDetailAsync(
        HttpClient client, Guid tenantId, Guid orderId)
    {
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{orderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<OrderResponse> ConvertAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    private static async Task<ExportWorkbookSheet> ExportOrdersAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var today = TodayInBogota();
        var range = $"convertedFrom={Iso(today.AddDays(-7))}&convertedTo={Iso(today.AddDays(1))}";
        var response = await client.PostAsync(
            $"{OrdersUrl(tenantId)}/export?{range}", content: null, TestContext.Current.CancellationToken);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(factory));
        return ExportWorkbookReader.Read(await factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{tenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
    }

    private static string Iso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record AcceptedDto(Guid JobId, DateTimeOffset RequestedAt);
}
