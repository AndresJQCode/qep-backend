using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec impuestos por tipo §7.5–§7.7 y §9.3 del lado del pedido: de dónde salen sus tasas al nacer (convertido, manual),
/// cómo las cambia la edición de un pedido <c>Pending</c> (D21, D22, D23) y qué trae la respuesta. Las tasas y la UVT se
/// siembran por SQL, como en <see cref="QuotationWithholdingsApiTests"/>; el resto, por la API.
/// </summary>
public sealed class OrderWithholdingsApiTests
{
    // Una UVT redonda: el umbral de la ReteICA (4 UVT) es 200.000 y una unidad del producto deja 100.000 de base.
    private const decimal Uvt = 50_000m;

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    [Fact]
    public async Task ConvertedOrderInheritsTheQuotationPhoto()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente, reteIca]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);

        var order = await ConvertAsync(setup, quotation.Id);

        // La misma foto, también la que no aplica (100.000 no llega a 4 UVT): la edición la prellena (D21).
        Assert.Equal(
            quotation.Withholdings.Select(row => (row.TaxRateId, row.Base, row.Amount, row.TaxUnitValue)).ToArray(),
            order.Withholdings.Select(row => (row.TaxRateId, row.Base, row.Amount, row.TaxUnitValue)).ToArray());
        Assert.Equal((retefuente, 2_500m), (Assert.Single(order.Withholdings).TaxRateId, order.RetentionAmount));
        Assert.Equal(new[] { retefuente, reteIca }, order.BillingWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Empty(order.CustomerWithholdingTaxRates);
        Assert.Equal(quotation.NetTotal, order.NetTotal);
        // Con los datos del cliente no hay parte propia.
        Assert.Null(order.BillingWithRetention);
    }

    // Spec §7.5 (pedido manual): las tasas del cliente, pero sólo las del país de la empresa facturadora. Con una empresa
    // española la Retefuente colombiana del cliente no viaja; el pedido se crea igual. La tabla de países sólo tiene
    // Colombia, así que una tasa de otro país no puede existir en el catálogo: el filtro se prueba moviendo la empresa.
    [Fact]
    public async Task ManualOrderTakesCustomerRatesOfItsCompanyCountry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        var spanish = await CreateSpanishCompanyAsync(setup.Client, setup.TenantId);

        var colombian = await CreateManualAsync(setup, clientId, setup.Account);
        var foreign = await CreateManualAsync(setup, clientId, spanish);

        Assert.Equal((retefuente, 2_500m), (Assert.Single(colombian.Withholdings).TaxRateId, colombian.RetentionAmount));
        Assert.Equal(new[] { retefuente }, colombian.BillingWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Equal(colombian.Total - 2_500m, colombian.NetTotal);
        Assert.Empty(foreign.Withholdings);
        Assert.Empty(foreign.BillingWithholdingTaxRates);
        Assert.Equal((0m, foreign.Total), (foreign.RetentionAmount, foreign.NetTotal));
    }

    // D21: la edición reemplaza las tasas del pedido; la ficha del cliente no se entera.
    [Fact]
    public async Task PendingEditChangesRatesAndNeverTouchesTheCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var compras = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [compras]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        var order = await ConvertAsync(setup, quotation.Id);

        var response = await PutEditsAsync(setup, order, Edits(setup, withholdingTaxRateIds: [servicios]));

        var saved = await ReadDetailAsync(response);
        Assert.Equal((servicios, 4_000m), (Assert.Single(saved.Order.Withholdings).TaxRateId, saved.Order.RetentionAmount));
        Assert.Equal(new[] { servicios }, saved.Order.BillingWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Contains(
            saved.History,
            entry => entry.EventType == "PartiesChanged"
                && entry.Details == "Cambió las retenciones de Retefuente 2,5 % a Retefuente servicios 4 %.");
        var fetched = await GetDetailAsync(setup, order.Id);
        Assert.Equal(servicios, Assert.Single(fetched.Order.Withholdings).TaxRateId);
        Assert.Equal(new[] { compras }, await CustomerRateIdsAsync(setup, clientId));

        // Una lista vacía las quita.
        var cleared = await ReadDetailAsync(await PutEditsAsync(setup, saved.Order, Edits(setup, withholdingTaxRateIds: [])));
        Assert.Empty(cleared.Order.BillingWithholdingTaxRates);
        Assert.Equal(0m, cleared.Order.RetentionAmount);
    }

    // D22: fijar la empresa en la edición habilita los selectores. Un pedido con empresa española no acepta la Retefuente
    // colombiana; la misma edición con la empresa colombiana en el cuerpo sí.
    [Fact]
    public async Task PendingEditSetsBillingAccountAndEnablesSelectors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var spanish = await CreateSpanishCompanyAsync(setup.Client, setup.TenantId);
        var created = await CreateManualAsync(setup, clientId, spanish);

        var rejected = await PutEditsAsync(setup, created, Edits(setup, withholdingTaxRateIds: [retefuente]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("order.billing.withholding_tax_rate_invalid", await CodeOfAsync(rejected));

        var response = await PutEditsAsync(
            setup, created, Edits(setup, withholdingTaxRateIds: [retefuente], billingAccount: setup.Account));

        var saved = await ReadDetailAsync(response);
        Assert.Equal(setup.Account.CompanyId, saved.Order.BillingAccount!.CompanyId);
        Assert.Equal((retefuente, 2_500m), (Assert.Single(saved.Order.Withholdings).TaxRateId, saved.Order.RetentionAmount));
        Assert.Contains(
            saved.History,
            entry => entry.EventType == "PartiesChanged"
                && entry.Details == $"Cambió la cuenta de cobro de {spanish.BankName} {spanish.AccountNumber} a {setup.Account.BankName} {setup.Account.AccountNumber}.");
        var fetched = await GetDetailAsync(setup, created.Id);
        Assert.Equal(setup.Account.CompanyId, fetched.Order.BillingAccount!.CompanyId);
        Assert.Equal(2_500m, fetched.Order.RetentionAmount);
    }

    // Plan P10 (owner, 2026-11-10): la cuenta no puede cambiar la moneda del pedido.
    [Fact]
    public async Task AnAccountInAnotherCurrencyIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var created = await CreateManualAsync(setup, clientId, setup.Account);
        var dollars = await CreateCompanyWithBankAccountAsync(setup.Client, setup.TenantId, "USD");

        var response = await PutEditsAsync(
            setup, created,
            Edits(setup, billingAccount: new QuotationBillingAccountRequest(
                dollars.CompanyId, dollars.BankName, dollars.AccountNumber, dollars.Currency)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.billing.currency_change_not_allowed", await CodeOfAsync(response));
    }

    // Review Focus 5 (D23): cambiar la empresa a otro país deja la tasa guardada sin aplicar; volver a mandarla es 422 y no
    // guarda nada.
    [Fact]
    public async Task ResendingAForeignRateAfterCountryChangeIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        var created = await CreateManualAsync(setup, clientId, setup.Account);
        var spanish = await CreateSpanishCompanyAsync(setup.Client, setup.TenantId);

        var moved = await ReadDetailAsync(await PutEditsAsync(setup, created, Edits(setup, billingAccount: spanish)));
        Assert.Equal(spanish.CompanyId, moved.Order.BillingAccount!.CompanyId);
        Assert.Equal(new[] { retefuente }, moved.Order.BillingWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Empty(moved.Order.Withholdings);
        Assert.Equal(0m, moved.Order.RetentionAmount);

        var resent = await PutEditsAsync(setup, moved.Order, Edits(setup, withholdingTaxRateIds: [retefuente]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resent.StatusCode);
        Assert.Equal("order.billing.withholding_tax_rate_invalid", await CodeOfAsync(resent));
        var fetched = await GetDetailAsync(setup, created.Id);
        Assert.Equal(moved.Order.Version, fetched.Order.Version);
        Assert.Equal(spanish.CompanyId, fetched.Order.BillingAccount!.CompanyId);
    }

    [Fact]
    public async Task PreviewEditReturnsTheSameWithholdingsTheSaveWouldStore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var spanish = await CreateSpanishCompanyAsync(setup.Client, setup.TenantId);
        var created = await CreateManualAsync(setup, clientId, spanish, quantity: 3m);
        var body = Edits(setup, withholdingTaxRateIds: [retefuente, reteIca], billingAccount: setup.Account, quantity: 3m);

        var preview = await setup.Client.PostAsJsonAsync(
            $"{OrdersUrl(setup.TenantId)}/{created.Id}/preview", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewed = await ReadDetailAsync(preview);
        Assert.Equal(created.Version, (await GetDetailAsync(setup, created.Id)).Order.Version);
        var saved = await ReadDetailAsync(await PutEditsAsync(setup, created, body));

        // Base 300.000: Retefuente 7.500 y ReteICA 2.898 (300.000 ≥ 4 × 50.000).
        Assert.Equal(
            new[] { (retefuente, 7_500m), (reteIca, 2_898m) },
            previewed.Order.Withholdings.Select(row => (row.TaxRateId, row.Amount)).ToArray());
        Assert.Equal(
            previewed.Order.Withholdings.Select(row => (row.TaxRateId, row.Base, row.Amount, row.TaxUnitValue)).ToArray(),
            saved.Order.Withholdings.Select(row => (row.TaxRateId, row.Base, row.Amount, row.TaxUnitValue)).ToArray());
        Assert.Equal(
            (previewed.Order.RetentionAmount, previewed.Order.NetTotal),
            (saved.Order.RetentionAmount, saved.Order.NetTotal));
    }

    [Fact]
    public async Task NonPendingEditIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory, ApproverPermissions);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var created = await CreateManualAsync(setup, clientId, setup.Account);
        var approve = await setup.Client.PostAsync(
            $"{OrdersUrl(setup.TenantId)}/{created.Id}/approve", null, TestContext.Current.CancellationToken);
        approve.EnsureSuccessStatusCode();
        var approved = await approve.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);

        var response = await PutEditsAsync(
            setup, approved!, Edits(setup, withholdingTaxRateIds: [retefuente], billingAccount: setup.Account));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_pending", await CodeOfAsync(response));
        Assert.Empty((await GetDetailAsync(setup, created.Id)).Order.BillingWithholdingTaxRates);
    }

    private sealed record Setup(
        HttpClient Client,
        Guid TenantId,
        Guid ProductId,
        QuotationBillingAccountRequest Account);

    // Tenant, empresa colombiana con cuenta en COP, UVT del año del documento y un producto de 119.000 con IVA del 19 %
    // adentro (base 100.000 por unidad).
    private static async Task<Setup> SetUpAsync(QepApiFactory factory, string[]? permissions = null)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, permissions ?? ManagerPermissions);
        await InsertTaxUnitValueAsync(factory, "CO", TodayInBogota().Year, Uvt);
        var company = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 119_000m, taxRateId: vat);
        return new Setup(
            client, tenantId, productId,
            new QuotationBillingAccountRequest(company.CompanyId, company.BankName, company.AccountNumber, company.Currency));
    }

    // Una empresa de afuera con cuenta en pesos: otro país sin cambiar la moneda del pedido (plan P10).
    private static async Task<QuotationBillingAccountRequest> CreateSpanishCompanyAsync(HttpClient client, Guid tenantId)
    {
        const string bankName = "Santander";
        var accountNumber = $"ES-{Random.Shared.Next(100000, 999999)}";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Iberia S.L.",
                bankAccounts = new[] { new { bankName, accountNumber, currency = "COP" } },
                taxId = $"B{Random.Shared.Next(10000000, 99999999)}",
                country = "ES",
                documentType = "NIF",
                cityName = "Madrid",
            },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return new QuotationBillingAccountRequest(json.RootElement.GetProperty("id").GetGuid(), bankName, accountNumber, "COP");
    }

    private static async Task<OrderResponse> ConvertAsync(Setup setup, Guid quotationId)
    {
        var response = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var converted = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(converted);
        return (await GetDetailAsync(setup, converted.Id)).Order;
    }

    private static async Task<OrderResponse> CreateManualAsync(
        Setup setup, Guid clientId, QuotationBillingAccountRequest account, decimal quantity = 1m)
    {
        var response = await setup.Client.PostAsJsonAsync(
            OrdersUrl(setup.TenantId),
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null, account,
                [new OrderEditItemRequest(setup.ProductId, quantity)], "PaymentPending", []),
            TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await ReadDetailAsync(response)).Order;
    }

    private static SaveOrderEditsRequest Edits(
        Setup setup,
        IReadOnlyList<Guid>? withholdingTaxRateIds = null,
        QuotationBillingAccountRequest? billingAccount = null,
        decimal quantity = 1m) =>
        new(
            [new OrderEditItemRequest(setup.ProductId, quantity)], null, null,
            WithholdingTaxRateIds: withholdingTaxRateIds,
            BillingAccount: billingAccount);

    private static async Task<HttpResponseMessage> PutEditsAsync(Setup setup, OrderResponse order, SaveOrderEditsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{OrdersUrl(setup.TenantId)}/{order.Id}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{order.Version}\"");
        return await setup.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {text}");
        var detail = JsonSerializer.Deserialize<OrderDetailResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<OrderDetailResponse> GetDetailAsync(Setup setup, Guid orderId)
    {
        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{orderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<Guid[]> CustomerRateIdsAsync(Setup setup, Guid customerId)
    {
        var customer = await setup.Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/tenants/{setup.TenantId}/customers/{customerId}", TestContext.Current.CancellationToken);
        return [.. customer.GetProperty("withholdingTaxRates").EnumerateArray().Select(rate => rate.GetProperty("id").GetGuid())];
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return problem.GetProperty("code").GetString()!;
    }
}
