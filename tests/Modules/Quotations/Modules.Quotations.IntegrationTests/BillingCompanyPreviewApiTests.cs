using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-11-16 withholdings by company: the four previews take <c>billingCompanyId</c> and, without a bank account,
/// paint the withholdings that company produces — the same ones the full account of that company paints — while the
/// response keeps <c>billingAccount: null</c> and readiness treats the account as absent.
///
/// Fixture: a CO company in the first seeded city («Bogotá» in the names) with «ReteICA Bogotá» 0,966 % assigned and a COP
/// account, a customer who is a withholding agent there, a product of 119.000 with 19 % VAT inside, tenant default COP.
/// </summary>
public sealed class BillingCompanyPreviewApiTests(SharedQuotationsHost sharedHost)
    : IClassFixture<SharedQuotationsHost>
{
    [Fact]
    public async Task NewQuotationPreviewWithCompanyOnlyPaintsTheSameWithholdingsAsWithItsAccount()
    {
        var fx = await SetUpAsync();

        var companyOnly = await ReadQuotationAsync(await PostNewQuotationPreviewAsync(
            fx, NewQuotationBody(fx, account: null) with { BillingCompanyId = fx.BogotaAccount.CompanyId }));
        var withAccount = await ReadQuotationAsync(await PostNewQuotationPreviewAsync(fx, NewQuotationBody(fx, fx.BogotaAccount)));

        Assert.Null(companyOnly.BillingAccount);
        Assert.NotNull(withAccount.BillingAccount);
        Assert.Equal("COP", companyOnly.Currency);
        Assert.Equal(fx.ReteIcaBogota, Assert.Single(companyOnly.Withholdings).TaxRateId);
        Assert.Equal(fx.Bogota.CityId, companyOnly.OperationCity?.Id);
        Assert.Equal(fx.Bogota.CityId, companyOnly.BillingCompanyCity?.Id);
        AssertSameWithholdings(withAccount, companyOnly);
    }

    [Fact]
    public async Task NewQuotationPreviewWithCompanyOnlyIsNotConvertible()
    {
        var fx = await SetUpAsync();

        var companyOnly = await ReadQuotationAsync(await PostNewQuotationPreviewAsync(
            fx, NewQuotationBody(fx, account: null) with { BillingCompanyId = fx.BogotaAccount.CompanyId }));
        var withAccount = await ReadQuotationAsync(await PostNewQuotationPreviewAsync(fx, NewQuotationBody(fx, fx.BogotaAccount)));

        // Control: the same body with the account is convertible, so only the missing account blocks it.
        Assert.True(withAccount.CanBeConvertedToOrder);
        Assert.False(companyOnly.CanBeConvertedToOrder);
    }

    [Fact]
    public async Task NewQuotationPreviewWithUnknownCompanyIs422()
    {
        var fx = await SetUpAsync();

        var response = await PostNewQuotationPreviewAsync(
            fx, NewQuotationBody(fx, account: null) with { BillingCompanyId = Guid.NewGuid() });

        Assert.Equal(QuotationBillingAccountVerifier.CompanyNotFoundCode, await CodeAsync(response));
    }

    [Fact]
    public async Task NewQuotationPreviewWithAccountAndCompanyIdUsesTheAccount()
    {
        var fx = await SetUpAsync();
        var other = await CreateCompanyWithBankAccountAsync(fx.Client, fx.TenantId, cityId: fx.Medellin.CityId);

        var preview = await ReadQuotationAsync(await PostNewQuotationPreviewAsync(
            fx, NewQuotationBody(fx, fx.BogotaAccount) with { BillingCompanyId = other.CompanyId }));

        Assert.Equal(fx.BogotaAccount.CompanyId, preview.BillingAccount?.CompanyId);
        Assert.Equal(fx.BogotaAccount.AccountNumber, preview.BillingAccount?.AccountNumber);
        Assert.Equal(fx.Bogota.CityId, preview.BillingCompanyCity?.Id);
    }

    [Fact]
    public async Task QuotationEditsPreviewWithCompanyOnlyPaintsItsWithholdings()
    {
        var fx = await SetUpAsync();
        // A draft saved without an account: the editor is still waiting for one.
        var draft = await ReadQuotationAsync(
            await fx.Client.PostAsJsonAsync(
                QuotationsUrl(fx.TenantId),
                new CreateQuotationRequest(
                    fx.ClientId, TodayInBogota().AddDays(30), null, null, null, null,
                    Items: [new QuotationEditItemRequest(fx.ProductId, 1m)]),
                TestContext.Current.CancellationToken),
            HttpStatusCode.Created);
        Assert.Null(draft.BillingAccount);
        Assert.Empty(draft.Withholdings);

        var companyOnly = await ReadQuotationAsync(await PostQuotationPreviewAsync(
            fx, draft, EditsBody(draft, account: null) with { BillingCompanyId = fx.BogotaAccount.CompanyId }));
        var withAccount = await ReadQuotationAsync(await PostQuotationPreviewAsync(fx, draft, EditsBody(draft, fx.BogotaAccount)));

        Assert.Null(companyOnly.BillingAccount);
        Assert.Equal("COP", companyOnly.Currency);
        Assert.Equal(fx.ReteIcaBogota, Assert.Single(companyOnly.Withholdings).TaxRateId);
        Assert.Equal(fx.Bogota.CityId, companyOnly.OperationCity?.Id);
        Assert.Equal(fx.Bogota.CityId, companyOnly.BillingCompanyCity?.Id);
        AssertSameWithholdings(withAccount, companyOnly);
        Assert.False(companyOnly.CanBeConvertedToOrder);
    }

    [Fact]
    public async Task NewOrderPreviewWithCompanyOnlyPaintsTheSameWithholdingsAsWithItsAccount()
    {
        var fx = await SetUpAsync();

        var companyOnly = await ReadOrderAsync(await PostNewOrderPreviewAsync(
            fx, NewOrderBody(fx, account: null) with { BillingCompanyId = fx.BogotaAccount.CompanyId }));
        var withAccount = await ReadOrderAsync(await PostNewOrderPreviewAsync(fx, NewOrderBody(fx, fx.BogotaAccount)));

        Assert.Null(companyOnly.BillingAccount);
        Assert.Null(companyOnly.Order.BillingAccount);
        Assert.NotNull(withAccount.BillingAccount);
        Assert.Equal("COP", companyOnly.Order.Currency);
        Assert.Equal(fx.ReteIcaBogota, Assert.Single(companyOnly.Order.Withholdings).TaxRateId);
        Assert.Equal(fx.Bogota.CityId, companyOnly.OperationCity?.Id);
        Assert.Equal(fx.Bogota.CityId, companyOnly.BillingCompanyCity?.Id);
        AssertSameWithholdings(withAccount, companyOnly);
    }

    // Review Focus: the saved order bills from company X; the edit preview names company Y without its account, so the
    // withholdings follow Y — the same ones Y's full account paints — and no account is answered.
    [Fact]
    public async Task OrderEditsPreviewWithCompanyOnlyResolvesAgainstThatCompany()
    {
        var fx = await SetUpAsync();
        var medellin = await CreateCompanyWithBankAccountAsync(fx.Client, fx.TenantId, cityId: fx.Medellin.CityId);
        var medellinRate = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Medellín", "ReteICA", 1m, cityId: fx.Medellin.CityId);
        await AssignCompanyWithholdingRateAsync(fx.Factory, fx.TenantId, medellin.CompanyId, medellinRate);
        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, fx.ClientId, fx.Medellin.CityId);
        var order = await CreateOrderAsync(fx);
        Assert.Equal(fx.ReteIcaBogota, Assert.Single(order.Order.Withholdings).TaxRateId);

        var companyOnly = await ReadOrderAsync(await PostOrderEditsPreviewAsync(
            fx, order, OrderEditsBody(fx) with { BillingCompanyId = medellin.CompanyId }));
        var withAccount = await ReadOrderAsync(await PostOrderEditsPreviewAsync(
            fx, order, OrderEditsBody(fx) with
            {
                BillingAccount = new QuotationBillingAccountRequest(
                    medellin.CompanyId, medellin.BankName, medellin.AccountNumber, medellin.Currency),
            }));

        Assert.Null(companyOnly.BillingAccount);
        Assert.Null(companyOnly.Order.BillingAccount);
        Assert.Equal(medellinRate, Assert.Single(companyOnly.Order.Withholdings).TaxRateId);
        Assert.Equal(fx.Medellin.CityId, companyOnly.OperationCity?.Id);
        Assert.Equal(fx.Medellin.CityId, companyOnly.BillingCompanyCity?.Id);
        AssertSameWithholdings(withAccount, companyOnly);
        // A preview never writes: the saved order still bills from X.
        Assert.Equal(fx.BogotaAccount.CompanyId, (await GetOrderAsync(fx, order.Order.Id)).BillingAccount?.CompanyId);
    }

    [Fact]
    public async Task OrderEditsPreviewWithoutCompanyOrAccountKeepsTheSavedAccount()
    {
        var fx = await SetUpAsync();
        var order = await CreateOrderAsync(fx);

        var preview = await ReadOrderAsync(await PostOrderEditsPreviewAsync(fx, order, OrderEditsBody(fx)));

        Assert.Equal(fx.BogotaAccount.CompanyId, preview.BillingAccount?.CompanyId);
        Assert.Equal(fx.BogotaAccount.AccountNumber, preview.BillingAccount?.AccountNumber);
        Assert.Equal(fx.BogotaAccount.CompanyId, preview.Order.BillingAccount?.CompanyId);
        Assert.Equal(fx.ReteIcaBogota, Assert.Single(preview.Order.Withholdings).TaxRateId);
        Assert.Equal(fx.Bogota.CityId, preview.BillingCompanyCity?.Id);
    }

    // The currency belongs to the missing account, not to the company: an edit preview builds the pending account in the
    // document's own currency (QuotationEditsApplication / OrderEdits pass quotation.Currency / order.Currency). The
    // company named here bills in COP, the tenant default is COP, and the saved document is in USD: it must stay USD.
    [Fact]
    public async Task OrderEditsPreviewWithCompanyOnlyKeepsTheOrdersOwnCurrency()
    {
        var fx = await SetUpAsync();
        var (usdAccount, usdProductId) = await UsdAccountAndProductAsync(fx);
        var order = await ReadOrderAsync(
            await fx.Client.PostAsJsonAsync(
                OrdersUrl(fx.TenantId),
                new CreateOrderRequest(
                    fx.ClientId, "Transferencia", null, false, null, null, usdAccount,
                    [new OrderEditItemRequest(usdProductId, 1m)], []),
                TestContext.Current.CancellationToken),
            HttpStatusCode.Created);
        Assert.Equal("USD", order.Order.Currency);

        var preview = await ReadOrderAsync(await PostOrderEditsPreviewAsync(
            fx, order,
            new PreviewOrderEditsRequest([new OrderEditItemRequest(usdProductId, 1m)], null, null) with
            {
                BillingCompanyId = fx.BogotaAccount.CompanyId,
            }));

        Assert.Equal("USD", preview.Order.Currency);
        Assert.Null(preview.BillingAccount);
        Assert.Null(preview.Order.BillingAccount);
        // The company's rates are explained, not applied: CO withholdings do not retain on a USD document.
        var row = Assert.Single(preview.Order.WithholdingBreakdown);
        Assert.Equal((fx.ReteIcaBogota, false, "CurrencyWithoutTax"), (row.TaxRateId, row.Applies, row.Reason));
        Assert.Empty(preview.Order.Withholdings);
        Assert.Equal(0m, preview.Order.RetentionAmount);
    }

    [Fact]
    public async Task QuotationEditsPreviewWithCompanyOnlyKeepsTheQuotationsOwnCurrency()
    {
        var fx = await SetUpAsync();
        var (usdAccount, usdProductId) = await UsdAccountAndProductAsync(fx);
        var draft = await ReadQuotationAsync(
            await fx.Client.PostAsJsonAsync(
                QuotationsUrl(fx.TenantId),
                new CreateQuotationRequest(
                    fx.ClientId, TodayInBogota().AddDays(30), null, null, null, usdAccount,
                    Items: [new QuotationEditItemRequest(usdProductId, 1m)]),
                TestContext.Current.CancellationToken),
            HttpStatusCode.Created);
        Assert.Equal("USD", draft.Currency);

        var preview = await ReadQuotationAsync(await PostQuotationPreviewAsync(
            fx, draft, EditsBody(draft, account: null) with { BillingCompanyId = fx.BogotaAccount.CompanyId }));

        Assert.Equal("USD", preview.Currency);
        Assert.Null(preview.BillingAccount);
        var row = Assert.Single(preview.WithholdingBreakdown);
        Assert.Equal((fx.ReteIcaBogota, false, "CurrencyWithoutTax"), (row.TaxRateId, row.Applies, row.Reason));
        Assert.Empty(preview.Withholdings);
        Assert.Equal(0m, preview.RetentionAmount);
    }

    // IsPending is [JsonIgnore] on the DTO and the composers null the account; this pins the wire contract of all four.
    [Fact]
    public async Task NoCompanyOnlyPreviewAnswersIsPending()
    {
        var fx = await SetUpAsync();
        var order = await CreateOrderAsync(fx);
        var saved = await ReadQuotationAsync(
            await fx.Client.PostAsJsonAsync(
                QuotationsUrl(fx.TenantId),
                new CreateQuotationRequest(
                    fx.ClientId, TodayInBogota().AddDays(30), null, null, null, null,
                    Items: [new QuotationEditItemRequest(fx.ProductId, 1m)]),
                TestContext.Current.CancellationToken),
            HttpStatusCode.Created);
        var company = fx.BogotaAccount.CompanyId;

        HttpResponseMessage[] responses =
        [
            await PostNewQuotationPreviewAsync(fx, NewQuotationBody(fx, account: null) with { BillingCompanyId = company }),
            await PostQuotationPreviewAsync(fx, saved, EditsBody(saved, account: null) with { BillingCompanyId = company }),
            await PostNewOrderPreviewAsync(fx, NewOrderBody(fx, account: null) with { BillingCompanyId = company }),
            await PostOrderEditsPreviewAsync(fx, order, OrderEditsBody(fx) with { BillingCompanyId = company }),
        ];

        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
            Assert.DoesNotContain("isPending", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Fixture ----

    // A USD account of a company in Bogotá and a product priced in USD, for a document whose currency is not the tenant's.
    private static async Task<(QuotationBillingAccountRequest Account, Guid ProductId)> UsdAccountAndProductAsync(Fixture fx)
    {
        var usd = await CreateCompanyWithBankAccountAsync(fx.Client, fx.TenantId, currency: "USD", cityId: fx.Bogota.CityId);
        var productId = await CreateProductWithScalesAsync(
            fx.Client, fx.TenantId, prices: new Dictionary<string, decimal> { ["COP"] = 119_000m, ["USD"] = 30m });
        return (new QuotationBillingAccountRequest(usd.CompanyId, usd.BankName, usd.AccountNumber, usd.Currency), productId);
    }

    private sealed record Fixture(
        QepApiFactory Factory,
        HttpClient Client,
        Guid TenantId,
        Guid ClientId,
        Guid ProductId,
        QuotationBillingAccountRequest BogotaAccount,
        CityInDepartment Bogota,
        CityInDepartment Medellin,
        Guid ReteIcaBogota);

    private async Task<Fixture> SetUpAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var (bogota, medellin) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var company = await CreateCompanyWithBankAccountAsync(client, tenantId, cityId: bogota.CityId);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 119_000m, taxRateId: vat);
        var reteIca = await InsertWithholdingRateAsync(
            factory, tenantId, "ReteICA Bogotá", "ReteICA", 0.966m, cityId: bogota.CityId);
        await AssignCompanyWithholdingRateAsync(factory, tenantId, company.CompanyId, reteIca);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        await SetWithholdingAgentAsync(factory, tenantId, clientId, bogota.CityId);
        return new Fixture(
            factory, client, tenantId, clientId, productId,
            new QuotationBillingAccountRequest(company.CompanyId, company.BankName, company.AccountNumber, company.Currency),
            bogota, medellin, reteIca);
    }

    // Everything the withholding cards paint: an account and a company-only preview of the same company must agree.
    private static void AssertSameWithholdings(QuotationResponse expected, QuotationResponse actual)
    {
        Assert.Equal(JsonOf(expected.Withholdings), JsonOf(actual.Withholdings));
        Assert.Equal(JsonOf(expected.WithholdingBreakdown), JsonOf(actual.WithholdingBreakdown));
        Assert.Equal(JsonOf(expected.MunicipalWithholdings), JsonOf(actual.MunicipalWithholdings));
        Assert.Equal(JsonOf(expected.OperationCity), JsonOf(actual.OperationCity));
        Assert.Equal(JsonOf(expected.BillingCompanyCity), JsonOf(actual.BillingCompanyCity));
        Assert.Equal((expected.RetentionAmount, expected.NetTotal), (actual.RetentionAmount, actual.NetTotal));
    }

    private static void AssertSameWithholdings(OrderDetailResponse expected, OrderDetailResponse actual)
    {
        Assert.Equal(JsonOf(expected.Order.Withholdings), JsonOf(actual.Order.Withholdings));
        Assert.Equal(JsonOf(expected.Order.WithholdingBreakdown), JsonOf(actual.Order.WithholdingBreakdown));
        Assert.Equal(JsonOf(expected.MunicipalWithholdings), JsonOf(actual.MunicipalWithholdings));
        Assert.Equal(JsonOf(expected.OperationCity), JsonOf(actual.OperationCity));
        Assert.Equal(JsonOf(expected.BillingCompanyCity), JsonOf(actual.BillingCompanyCity));
        Assert.Equal(
            (expected.Order.RetentionAmount, expected.Order.NetTotal), (actual.Order.RetentionAmount, actual.Order.NetTotal));
    }

    private static string JsonOf<T>(T value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static PreviewOrderRequest NewOrderBody(Fixture fx, QuotationBillingAccountRequest? account) =>
        new(fx.ClientId, "Transferencia", null, false, null, null, account, [new OrderEditItemRequest(fx.ProductId, 1m)], []);

    private static PreviewOrderEditsRequest OrderEditsBody(Fixture fx) =>
        new([new OrderEditItemRequest(fx.ProductId, 1m)], null, null);

    private static async Task<OrderDetailResponse> CreateOrderAsync(Fixture fx) =>
        await ReadOrderAsync(
            await fx.Client.PostAsJsonAsync(
                OrdersUrl(fx.TenantId),
                new CreateOrderRequest(
                    fx.ClientId, "Transferencia", null, false, null, null, fx.BogotaAccount,
                    [new OrderEditItemRequest(fx.ProductId, 1m)], []),
                TestContext.Current.CancellationToken),
            HttpStatusCode.Created);

    private static async Task<OrderDetailResponse> GetOrderAsync(Fixture fx, Guid orderId) =>
        await ReadOrderAsync(
            await fx.Client.GetAsync($"{OrdersUrl(fx.TenantId)}/{orderId}", TestContext.Current.CancellationToken));

    private static Task<HttpResponseMessage> PostNewOrderPreviewAsync(Fixture fx, PreviewOrderRequest body) =>
        fx.Client.PostAsJsonAsync($"{OrdersUrl(fx.TenantId)}/preview", body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostOrderEditsPreviewAsync(
        Fixture fx, OrderDetailResponse order, PreviewOrderEditsRequest body) =>
        fx.Client.PostAsJsonAsync(
            $"{OrdersUrl(fx.TenantId)}/{order.Order.Id}/preview", body, TestContext.Current.CancellationToken);

    private static async Task<OrderDetailResponse> ReadOrderAsync(
        HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        var detail = JsonSerializer.Deserialize<OrderDetailResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(detail);
        return detail;
    }

    private static PreviewNewQuotationRequest NewQuotationBody(Fixture fx, QuotationBillingAccountRequest? account) =>
        new(
            fx.ClientId, TodayInBogota().AddDays(30), null, null, null, account,
            Items: [new QuotationEditItemRequest(fx.ProductId, 1m)]);

    private static PreviewQuotationRequest EditsBody(QuotationResponse quotation, QuotationBillingAccountRequest? account) =>
        new(
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Notes,
            null,
            account,
            [.. quotation.Items.Select(item => new QuotationEditItemRequest(item.VariantId, item.Quantity))]);

    private static Task<HttpResponseMessage> PostNewQuotationPreviewAsync(Fixture fx, PreviewNewQuotationRequest body) =>
        fx.Client.PostAsJsonAsync($"{QuotationsUrl(fx.TenantId)}/preview", body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostQuotationPreviewAsync(
        Fixture fx, QuotationResponse quotation, PreviewQuotationRequest body) =>
        fx.Client.PostAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/{quotation.Id}/preview", body, TestContext.Current.CancellationToken);

    private static async Task<QuotationResponse> ReadQuotationAsync(
        HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        var quotation = JsonSerializer.Deserialize<QuotationResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        return body.RootElement.GetProperty("code").GetString();
    }
}
