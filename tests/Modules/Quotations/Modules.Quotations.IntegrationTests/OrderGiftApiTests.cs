using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Regalos en el pedido (spec 2026-11-21): entran por su propia ruta y no suben el total.</summary>
public sealed class OrderGiftApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    private static string GiftsUrl(Guid tenantId, Guid orderId) => $"{OrderByIdUrl(tenantId, orderId)}/gifts";

    [Fact]
    public async Task PostGiftThenGetOrderReturnsIsGiftAndSameTotal()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        // 1.000 con IVA incluido, sin escalas: 10 unidades suman 10.000.
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 1_000m, scales: NoDiscountScales());
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(productId, 10m)], []),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = await created.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal(10_000m, order.Order.Total);

        // El regalo es de la misma variante que la línea pagada.
        var response = await client.PostAsJsonAsync(
            GiftsUrl(tenantId, order.Order.Id),
            new AddOrderGiftRequest(productId, 1m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, order.Order.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(10_000m, fetched.Order.Total);
        Assert.Equal(2, fetched.Items.Count);
        var gift = Assert.Single(fetched.Items, item => item.IsGift);
        Assert.Equal(productId, gift.VariantId);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Single(fetched.Items, item => !item.IsGift);

        var removed = await client.DeleteAsync(
            $"{GiftsUrl(tenantId, order.Order.Id)}/{gift.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var afterRemoval = await removed.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(afterRemoval);
        Assert.Single(afterRemoval.Items);
        Assert.Equal(10_000m, afterRemoval.Order.Total);
    }
}
