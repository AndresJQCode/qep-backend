using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Politica de campos del cliente: aprobar un pedido exige el documento del cliente facturado solo si el
/// tenant lo pide al facturar.</summary>
public sealed class BilledCustomerFieldsApiTests
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
                [new OrderEditItemRequest(productId, 1m)], "PaymentPending", []),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    [Fact]
    public async Task ApprovingAnOrderOfACustomerWithoutDocumentFollowsThePolicy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        await SetTenantCustomerFieldPolicyAsync(factory, tenantId, false, false, true, true);
        var clientId = await CreateActiveCustomerAsync(client, tenantId, withDocument: false);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var first = await CreateManualOrderAsync(client, tenantId, clientId, productId);
        var second = await CreateManualOrderAsync(client, tenantId, clientId, productId);

        var approved = await client.PostAsync(
            $"{OrderUrl(tenantId, first.Order.Id)}/approve", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await SetTenantCustomerFieldPolicyAsync(factory, tenantId, false, true, true, true);
        var rejected = await client.PostAsync(
            $"{OrderUrl(tenantId, second.Order.Id)}/approve", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using var body = JsonDocument.Parse(
            await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("order.order.billed_document_required", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("Customer", body.RootElement.GetProperty("missingOn").GetString());
    }
}
