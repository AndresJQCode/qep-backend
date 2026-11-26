using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La cuenta de cobro opcional (spec 2026-11-17 §4.2, R3): con la empresa y sin banco ni número, una cotización se envía y
/// se convierte, y un pedido manual se crea en la moneda por defecto del tenant. La cuenta del pedido conserva la empresa
/// con banco y número nulos.
/// </summary>
public sealed class OptionalBillingAccountApiTests(SharedQuotationsHost sharedHost)
    : IClassFixture<SharedQuotationsHost>
{
    [Fact]
    public async Task AQuotationIsSentAndConvertedWithACompanyAndNoAccount()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var company = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(
            client, tenantId, clientId, billingAccount: new QuotationBillingAccountRequest(company.CompanyId, null, null, null));
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);

        var sent = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        var text = await converted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(converted.StatusCode == HttpStatusCode.Created, $"{(int)converted.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        var account = body.RootElement.GetProperty("order").GetProperty("billingAccount");
        Assert.Equal(company.CompanyId, account.GetProperty("companyId").GetGuid());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("bankName").ValueKind);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("accountNumber").ValueKind);
    }

    // Review Focus 5: la vista previa con empresa y sin cuenta, que antes respondía billingAccount: null.
    [Fact]
    public async Task ACompanyOnlyPreviewAnswersTheCompanyWithNullBankFields()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var company = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders/preview",
            new PreviewOrderRequest(
                clientId, "Transferencia", null, false, null, null, null,
                [new OrderEditItemRequest(productId, 1m)], []) with
            { BillingCompanyId = company.CompanyId },
            TestContext.Current.CancellationToken);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        var account = body.RootElement.GetProperty("billingAccount");
        Assert.Equal(company.CompanyId, account.GetProperty("companyId").GetGuid());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("bankName").ValueKind);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("accountNumber").ValueKind);
        Assert.True(account.GetProperty("chargesVat").GetBoolean());
    }

    [Fact]
    public async Task TheDetailReturnsChargesVat()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var cityId = await EnsureCityIdAsync(client);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        using var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Régimen Simple S.A.S.",
                bankAccounts = Array.Empty<object>(),
                taxId = "901.222.333-4",
                country = "CO",
                documentType = "NIT",
                cityId,
                chargesVat = false,
            },
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        using var companyBody = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var companyId = companyBody.RootElement.GetProperty("id").GetGuid();
        var quotation = await CreateQuotationAsync(
            client, tenantId, clientId, billingAccount: new QuotationBillingAccountRequest(companyId, null, null, null));

        var text = await client.GetStringAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(text);
        var account = body.RootElement.GetProperty("billingAccount");
        Assert.False(account.GetProperty("chargesVat").GetBoolean());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("bankName").ValueKind);
    }

    // Sin cuenta, la moneda es la por defecto del tenant (COP), aunque el request traiga otra: la decide el documento (R3).
    [Fact]
    public async Task AManualOrderIsCreatedWithoutBankAccountInTheTenantCurrency()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var company = await CreateCompanyWithBankAccountAsync(client, tenantId, currency: "USD");
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(company.CompanyId, null, null, "USD"),
                [new OrderEditItemRequest(productId, 1m)], []),
            TestContext.Current.CancellationToken);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {text}");
        var detail = JsonSerializer.Deserialize<OrderDetailResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(detail);
        Assert.Equal("COP", detail.Order.Currency);
        Assert.Equal(company.CompanyId, detail.Order.BillingAccount?.CompanyId);
        Assert.Null(detail.Order.BillingAccount?.BankName);
        Assert.Null(detail.BillingAccount?.AccountNumber);
    }
}
