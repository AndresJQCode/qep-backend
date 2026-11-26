using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// IVA por empresa contra Postgres real (spec 2026-11-17 §5.2, R2): una empresa sin IVA cobra el precio de lista y su envío
/// no pide la tasa del tenant; pasar a una empresa con IVA con el envío cobrado sí la pide; y un pedido emitido conserva su
/// IVA aunque la empresa cambie después.
/// </summary>
public sealed class CompanyVatApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    [Fact]
    public async Task AQuotationOfACompanyWithoutVatChargesTheListPriceAndNoFreightTax()
    {
        var fx = await SetUpAsync();

        using var response = await CreateQuotationWithFreightAsync(fx, fx.WithoutVat);

        var quotation = await ReadAsync<QuotationResponse>(response, HttpStatusCode.Created);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(0m, quotation.FreightTaxAmount);
        Assert.Equal(119_000m + 15_000m, quotation.Total);
        Assert.Equal(0, Assert.Single(quotation.Items).TaxPercentage);
    }

    // Review Focus 3: volver a IVA con el envío cobrado necesita la tasa del envío del tenant, y este no tiene.
    [Fact]
    public async Task TurningVatOnWithoutTenantFreightRateIs422()
    {
        var fx = await SetUpAsync();
        using var created = await CreateQuotationWithFreightAsync(fx, fx.WithoutVat);
        var quotation = await ReadAsync<QuotationResponse>(created, HttpStatusCode.Created);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{QuotationsUrl(fx.TenantId)}/{quotation.Id}")
        {
            Content = JsonContent.Create(new SaveQuotationRequest(
                quotation.ValidUntil,
                quotation.PaymentMethod,
                quotation.Notes,
                null,
                fx.WithVat,
                [new QuotationEditItemRequest(fx.ProductId, 1m)],
                Shipping: new ShippingRequest(null, "Charged", 15_000m, false))),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        using var response = await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal("quotation.quotation.freight_tax_rate_missing", body.RootElement.GetProperty("code").GetString());
    }

    // Anti-objetivo del spec §2: cambiar después la configuración de la empresa no cambia un documento emitido.
    [Fact]
    public async Task ChangingTheCompanyLaterDoesNotChangeAnIssuedOrder()
    {
        var fx = await SetUpAsync();
        var created = await fx.Client.PostAsJsonAsync(
            $"/api/v1/tenants/{fx.TenantId}/orders",
            new CreateOrderRequest(
                fx.ClientId, "Transferencia", null, false, null, null, fx.WithoutVat,
                [new OrderEditItemRequest(fx.ProductId, 1m)], []),
            TestContext.Current.CancellationToken);
        var order = await ReadAsync<OrderDetailResponse>(created, HttpStatusCode.Created);
        Assert.Equal(0m, order.Order.TaxAmount);

        using var update = await fx.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{fx.TenantId}/companies/{fx.WithoutVat.CompanyId}",
            CompanyBody(fx.CityId, fx.WithoutVat, chargesVat: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var reread = await ReadAsync<OrderDetailResponse>(
            await fx.Client.GetAsync(
                $"/api/v1/tenants/{fx.TenantId}/orders/{order.Order.Id}", TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        Assert.Equal(0m, reread.Order.TaxAmount);
    }

    // ---- Fixture ----

    private sealed record Fixture(
        HttpClient Client,
        Guid TenantId,
        Guid ClientId,
        Guid ProductId,
        Guid CityId,
        QuotationBillingAccountRequest WithoutVat,
        QuotationBillingAccountRequest WithVat);

    // Un tenant sin tasa de IVA del envío, un producto de 119.000 con IVA 19 % adentro y dos empresas: una que no cobra
    // IVA y otra que sí.
    private async Task<Fixture> SetUpAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var cityId = await EnsureCityIdAsync(client);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 119_000m, taxRateId: vat);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var withoutVat = await CreateCompanyAsync(client, tenantId, cityId, chargesVat: false);
        var withVat = await CreateCompanyAsync(client, tenantId, cityId, chargesVat: true);
        // La empresa con IVA fija «IVA 19%» como tasa del envío (spec 2026-11-17 §5.3); el fixture la quiere vacía.
        await ClearFreightTaxRateAsync(factory, tenantId);
        return new Fixture(client, tenantId, clientId, productId, cityId, withoutVat, withVat);
    }

    private static object CompanyBody(Guid cityId, QuotationBillingAccountRequest account, bool chargesVat) => new
    {
        name = "QEP Régimen Simple S.A.S.",
        bankAccounts = new[] { new { bankName = account.BankName, accountNumber = account.AccountNumber, currency = "COP" } },
        taxId = "901.555.444-2",
        country = "CO",
        documentType = "NIT",
        cityId,
        chargesVat,
    };

    private static async Task<QuotationBillingAccountRequest> CreateCompanyAsync(
        HttpClient client, Guid tenantId, Guid cityId, bool chargesVat)
    {
        var account = new QuotationBillingAccountRequest(
            Guid.Empty, "Bancolombia", $"{Random.Shared.Next(100000000, 999999999)}", "COP");
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            CompanyBody(cityId, account, chargesVat),
            TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal(chargesVat, body.RootElement.GetProperty("chargesVat").GetBoolean());
        return account with { CompanyId = body.RootElement.GetProperty("id").GetGuid() };
    }

    private static Task<HttpResponseMessage> CreateQuotationWithFreightAsync(
        Fixture fx, QuotationBillingAccountRequest account) =>
        fx.Client.PostAsJsonAsync(
            QuotationsUrl(fx.TenantId),
            new CreateQuotationRequest(
                fx.ClientId, TodayInBogota().AddDays(30), null, null, null, account,
                Shipping: new ShippingRequest(null, "Charged", 15_000m, false),
                Items: [new QuotationEditItemRequest(fx.ProductId, 1m)]),
            TestContext.Current.CancellationToken);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        var value = JsonSerializer.Deserialize<T>(text, JsonSerializerOptions.Web);
        Assert.NotNull(value);
        return value;
    }
}
