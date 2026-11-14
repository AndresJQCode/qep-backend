using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Politica de campos del cliente: marcar un pedido como pagado exige el documento del cliente facturado
/// solo si el tenant lo pide al facturar (spec 2026-11-10 §3.2; antes, al aprobar).</summary>
public sealed class BilledCustomerFieldsApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static string OrderUrl(Guid tenantId, Guid orderId) => $"{OrdersUrl(tenantId)}/{orderId}";

    private static async Task<OrderDetailResponse> CreateManualOrderAsync(
        HttpClient client, Guid tenantId, Guid clientId, Guid productId)
    {
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var response = await client.PostAsJsonAsync(
            OrdersUrl(tenantId),
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(productId, 1m)], []),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    [Fact]
    public async Task MarkingPaidAnOrderOfACustomerWithoutDocumentFollowsThePolicy()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        await SetTenantCustomerFieldPolicyAsync(factory, tenantId, false, false, true, true);
        var clientId = await CreateActiveCustomerAsync(client, tenantId, withDocument: false);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var first = await CreateManualOrderAsync(client, tenantId, clientId, productId);
        var second = await CreateManualOrderAsync(client, tenantId, clientId, productId);

        var approved = await client.PostAsync(
            $"{OrderUrl(tenantId, first.Order.Id)}/mark-paid", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await SetTenantCustomerFieldPolicyAsync(factory, tenantId, false, true, true, true);
        var rejected = await client.PostAsync(
            $"{OrderUrl(tenantId, second.Order.Id)}/mark-paid", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using var body = JsonDocument.Parse(
            await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("order.order.billed_document_required", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("Customer", body.RootElement.GetProperty("missingOn").GetString());
    }

    // Prueba de caracterizacion (sin RED/GREEN): fija que un pedido sin flete no pide la ciudad del cliente
    // aunque el tenant la exija al facturar, y que lo guardado es ShippingDetails.None.
    [Fact]
    public async Task MarkingPaidAnOrderWithoutFreightPassesForACustomerWithoutCityUnderACityOnBillingPolicy()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        await SetTenantCustomerFieldPolicyAsync(factory, tenantId, false, false, false, true);
        var classificationId = await CreateClassificationAsync(client, tenantId);
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                phone = "310 935 2187",
                email = "compras@verde.co",
                address = "Calle 10 # 45-12",
                country = "CO",
                personType = (string?)null,
                classificationId,
                withRetention = false,
                vatSurplus = false
            },
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        using var customerBody = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var customerId = customerBody.RootElement.GetProperty("id").GetGuid();
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var order = await CreateManualOrderAsync(client, tenantId, customerId, productId);

        var approved = await client.PostAsync(
            $"{OrderUrl(tenantId, order.Order.Id)}/mark-paid", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var stored = await dbContext.Orders.AsNoTracking().SingleAsync(
            row => row.Id == new OrderId(order.Order.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(stored.PaidMarkedAt);
        Assert.Equal(ShippingDetails.None, stored.ShippingDetails);
    }
}
