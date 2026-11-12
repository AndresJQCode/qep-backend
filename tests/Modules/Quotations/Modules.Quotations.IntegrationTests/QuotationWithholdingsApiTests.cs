using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec impuestos por tipo §7.5–§7.7 y §9.3: la cotización congela las tasas del cliente al crearse, la facturación
/// propia elige las suyas con selectores, la respuesta trae cada retención aplicada y el frontend viejo sigue
/// funcionando con <c>billingWithRetention</c> derivado. Las tasas y la UVT se siembran por SQL (ver
/// <see cref="QuotationsApiHarness.InsertWithholdingRateAsync"/>); el cliente y la cotización, por la API.
/// </summary>
public sealed class QuotationWithholdingsApiTests
{
    // Una UVT redonda para que el umbral de la ReteICA (4 UVT = 200.000) se lea en las aserciones.
    private const decimal Uvt = 50_000m;

    [Fact]
    public async Task TwoWithholdingsGiveTwoLinesAndTheirSum()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente, reteIca]);

        var quotation = await CreateWithLinesAsync(setup, clientId, quantity: 3m);

        // Base = 300.000 (sin IVA): Retefuente 7.500; ReteICA 2.898 (300.000 ≥ 4 × 50.000).
        Assert.Equal(300_000m, quotation.Subtotal);
        Assert.Equal(
            new[]
            {
                (retefuente, "Retefuente", (decimal?)null, (string?)null, (decimal?)null, 300_000m, 7_500m),
                (reteIca, "ReteICA", 4m, "UVT", Uvt, 300_000m, 2_898m),
            },
            quotation.Withholdings
                .Select(row => (row.TaxRateId, row.Type, row.MinimumBase, row.MinimumBaseUnit, row.TaxUnitValue, row.Base, row.Amount))
                .ToArray());
        Assert.Equal(10_398m, quotation.RetentionAmount);
        Assert.Equal(quotation.Total - 10_398m, quotation.NetTotal);
        Assert.Equal(new[] { retefuente, reteIca }, quotation.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Empty(quotation.BillingWithholdingTaxRates);
        // Con los datos del cliente no hay parte propia: null, como siempre.
        Assert.Null(quotation.BillingWithRetention);
    }

    [Fact]
    public async Task SelectorRetefuenteServiciosAppliesFourPercent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var compras = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [compras]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        var response = await PutAsync(setup, created, OwnBilling(setup, withholdingTaxRateIds: [servicios]));

        var saved = await ReadAsync(response);
        var applied = Assert.Single(saved.Withholdings);
        Assert.Equal((servicios, 100_000m, 4_000m), (applied.TaxRateId, applied.Base, applied.Amount));
        Assert.Equal(4_000m, saved.RetentionAmount);
        Assert.Equal(new[] { servicios }, saved.BillingWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        // La foto del cliente sigue ahí, sin aplicar: vuelve a mandar si se vuelve a sus datos.
        Assert.Equal(new[] { compras }, saved.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.True(saved.BillingWithRetention);
    }

    // D17/D24: la tasa elegida queda congelada al elegirla. Reenviar los mismos selectores (lo que hace todo guardado)
    // no vuelve a leer el catálogo: un porcentaje editado después no pisa la foto.
    [Fact]
    public async Task ResendingTheSameSelectorsKeepsTheFrozenRate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);
        var selected = await ReadAsync(await PutAsync(setup, created, OwnBilling(setup, withholdingTaxRateIds: [servicios])));
        await ChangeTaxRateAsync(factory, servicios, percentage: 6m);

        var resaved = await ReadAsync(await PutAsync(setup, selected, OwnBilling(setup, withholdingTaxRateIds: [servicios])));

        var applied = Assert.Single(resaved.Withholdings);
        Assert.Equal((servicios, 4m, 100_000m, 4_000m), (applied.TaxRateId, applied.Percentage, applied.Base, applied.Amount));
        Assert.Equal(4_000m, resaved.RetentionAmount);
    }

    // Review Focus 2: el frontend viejo manda billingWithRetention y nunca billingWithholdingTaxRateIds. Lo guardado se
    // conserva, la respuesta nunca trae null con parte propia (su pantalla bloquea la conversión con null) y convertir
    // no queda bloqueado.
    [Fact]
    public async Task OldFrontendBodyKeepsSelectorsAndNeverNullsBillingWithRetention()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var withSelector = await ReadAsync(await PutAsync(
            setup, await CreateWithLinesAsync(setup, clientId, quantity: 1m), OwnBilling(setup, withholdingTaxRateIds: [servicios])));
        var withoutSelector = await ReadAsync(await PutAsync(
            setup, await CreateWithLinesAsync(setup, clientId, quantity: 1m), OwnBilling(setup, withholdingTaxRateIds: [])));

        var keptJson = await PutOldFrontendBodyAsync(setup, withSelector);
        var emptyJson = await PutOldFrontendBodyAsync(setup, withoutSelector);

        Assert.True(keptJson.GetProperty("billingWithRetention").GetBoolean());
        Assert.Equal(4_000m, keptJson.GetProperty("retentionAmount").GetDecimal());
        Assert.Equal(servicios, keptJson.GetProperty("billingWithholdingTaxRates")[0].GetProperty("id").GetGuid());
        // Pidió retención con el interruptor viejo y no tiene selectores: el interruptor ya no tiene efecto (spec §12),
        // pero la respuesta dice false, no null.
        Assert.Equal(JsonValueKind.False, emptyJson.GetProperty("billingWithRetention").ValueKind);
        Assert.Equal(0m, emptyJson.GetProperty("retentionAmount").GetDecimal());

        var sent = await SendAsync(factory, setup, withoutSelector.Id);
        Assert.True(sent.CanBeConvertedToOrder);
        var conversion = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{withoutSelector.Id}/order",
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        Assert.True(
            conversion.StatusCode == HttpStatusCode.Created,
            await conversion.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetNoLongerWrites()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m);
        const string identification = "900.111.222-5";
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, identification, withholdingTaxRateIds: [retefuente]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        await UpdateCustomerRetentionAsync(
            setup.Client, setup.TenantId, clientId, identification, withRetention: true, vatSurplus: true,
            withholdingTaxRateIds: [reteIca]);
        var fetched = await GetAsync(setup, created.Id);

        Assert.Equal(created.Version, fetched.Version);
        Assert.Equal(created.UpdatedAt, fetched.UpdatedAt);
        Assert.Equal((2_500m, created.Total, false), (fetched.RetentionAmount, fetched.Total, fetched.CustomerVatSurplus));
        Assert.Equal(new[] { retefuente }, fetched.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
    }

    // D17, D24: editar la ficha del cliente no mueve una cotización en borrador, ni al guardarla de nuevo: ni sus tasas
    // ni su excedente de IVA. Sólo cambiar de cliente vuelve a copiar.
    [Fact]
    public async Task EditingTheCustomerDoesNotMoveADraft()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m);
        const string identification = "900.111.222-6";
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, identification, withholdingTaxRateIds: [retefuente]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        await UpdateCustomerRetentionAsync(
            setup.Client, setup.TenantId, clientId, identification, withRetention: false, vatSurplus: true,
            withholdingTaxRateIds: [reteIca]);
        var saved = await ReadAsync(await PutAsync(setup, created, parties: null));

        Assert.Equal(new[] { retefuente }, saved.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Equal(2_500m, saved.RetentionAmount);
        Assert.False(saved.CustomerVatSurplus);
        Assert.Equal(created.Total, saved.Total);
    }

    [Fact]
    public async Task ChangingTheClientRecopiesItsRates()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m);
        var firstClient = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        var secondClient = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [reteIca]);
        var created = await CreateWithLinesAsync(setup, firstClient, quantity: 1m);

        var response = await setup.Client.PutAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{created.Id}/client",
            new { clientId = secondClient },
            TestContext.Current.CancellationToken);

        var changed = await ReadAsync(response);
        Assert.Equal(new[] { reteIca }, changed.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Equal(966m, changed.RetentionAmount);
    }

    [Fact]
    public async Task PreviewsReturnTheSameFields()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);

        var newPreview = await ReadAsync(await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/preview",
            new CreateQuotationRequest(
                clientId, null, null, null, null, setup.Account, Items: [new QuotationEditItemRequest(setup.ProductId, 1m)]),
            TestContext.Current.CancellationToken));
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);
        var editPreview = await ReadAsync(await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{created.Id}/preview",
            SaveBody(created, OwnBilling(setup, withholdingTaxRateIds: [servicios])),
            TestContext.Current.CancellationToken));

        Assert.Equal((retefuente, 2_500m), (Assert.Single(newPreview.Withholdings).TaxRateId, newPreview.RetentionAmount));
        Assert.Equal(new[] { retefuente }, newPreview.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Equal((servicios, 4_000m), (Assert.Single(editPreview.Withholdings).TaxRateId, editPreview.RetentionAmount));
        Assert.Equal(new[] { servicios }, editPreview.BillingWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.True(editPreview.BillingWithRetention);
        // El cálculo previo no escribe.
        Assert.Empty((await GetAsync(setup, created.Id)).BillingWithholdingTaxRates);
    }

    [Fact]
    public async Task SendWithoutCompanyIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var quotation = await CreateQuotationAsync(setup.Client, setup.TenantId, clientId);
        await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(setup.ProductId, 1m),
            TestContext.Current.CancellationToken);

        var response = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(await CreateAvailablePdfFileAsync(setup.Client, factory, setup.TenantId)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("quotation.billing.account_required", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASelectorOfAnotherTypeOfRateIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var compras = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        var repeated = await PutAsync(setup, created, OwnBilling(setup, withholdingTaxRateIds: [compras, servicios]));
        var vat = await PutAsync(setup, created, OwnBilling(setup, withholdingTaxRateIds: [setup.VatRateId]));

        Assert.Equal("quotation.billing.withholding_type_repeated", await CodeAsync(repeated));
        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", await CodeAsync(vat));
    }

    [Fact]
    public async Task ClientWithRetentionIsDerived()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m);
        var withRetefuente = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente, reteIca]);
        // El cuerpo dice withRetention true y se ignora: sólo una Retefuente lo prende.
        var reteIcaOnly = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withRetention: true, withholdingTaxRateIds: [reteIca]);

        var first = await CreateQuotationAsync(setup.Client, setup.TenantId, withRetefuente);
        var second = await CreateQuotationAsync(setup.Client, setup.TenantId, reteIcaOnly);

        Assert.True(first.Client!.WithRetention);
        Assert.False(second.Client!.WithRetention);
    }

    // La tasa sólo queda en la foto de la cotización: Catalog la deja desactivar (in_use cuenta clientes, no documentos)
    // y la cotización la sigue mostrando.
    [Fact]
    public async Task RateFrozenOnlyInAQuotationCanBeDeactivated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        const string identification = "900.111.222-7";
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, identification, withholdingTaxRateIds: [retefuente]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);
        await UpdateCustomerRetentionAsync(
            setup.Client, setup.TenantId, clientId, identification, withRetention: false, vatSurplus: false,
            withholdingTaxRateIds: []);

        var deactivate = await setup.Client.PostAsync(
            $"/api/v1/tenants/{setup.TenantId}/catalog/tax-rates/{retefuente}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        var fetched = await GetAsync(setup, created.Id);
        Assert.Equal(retefuente, Assert.Single(fetched.Withholdings).TaxRateId);
        Assert.Equal(2_500m, fetched.RetentionAmount);
    }

    private sealed record Setup(
        HttpClient Client,
        Guid TenantId,
        Guid ProductId,
        Guid VatRateId,
        QuotationBillingAccountRequest Account,
        CityInDepartment City);

    // Tenant, empresa colombiana con cuenta en COP, UVT del año del documento y un producto de 119.000 con IVA del 19 %
    // adentro (base 100.000 por unidad).
    private static async Task<Setup> SetUpAsync(QepApiFactory factory)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        await InsertTaxUnitValueAsync(factory, "CO", TodayInBogota().Year, Uvt);
        var company = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 119_000m, taxRateId: vat);
        var (city, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        return new Setup(
            client, tenantId, productId, vat,
            new QuotationBillingAccountRequest(company.CompanyId, company.BankName, company.AccountNumber, company.Currency),
            city);
    }

    private static async Task<QuotationResponse> CreateWithLinesAsync(Setup setup, Guid clientId, decimal quantity)
    {
        var quotation = await CreateQuotationAsync(setup.Client, setup.TenantId, clientId, billingAccount: setup.Account);
        var response = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(setup.ProductId, quantity),
            TestContext.Current.CancellationToken);
        return await ReadAsync(response, HttpStatusCode.Created);
    }

    // Completa (teléfono, correo, dirección y ciudad): una parte incompleta no deja convertir.
    private static QuotationPartiesRequest OwnBilling(Setup setup, IReadOnlyList<Guid>? withholdingTaxRateIds) =>
        new(
            new QuotationPartyRequest(
                "Sede administrativa", "1020304050", "3001234567", "sede@verde.co", "Calle 80 # 10-20",
                setup.City.DepartmentId, setup.City.CityId, PersonType: "Natural", IdentificationType: "CC"),
            null,
            BillingVatSurplus: false,
            BillingWithholdingTaxRateIds: withholdingTaxRateIds);

    private static SaveQuotationRequest SaveBody(QuotationResponse quotation, QuotationPartiesRequest? parties) =>
        new(
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Notes,
            parties,
            quotation.BillingAccount is { } billing
                ? new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency)
                : null,
            [.. quotation.Items.Select(item => new QuotationEditItemRequest(item.VariantId, item.Quantity))]);

    private static async Task<HttpResponseMessage> PutAsync(
        Setup setup, QuotationResponse quotation, QuotationPartiesRequest? parties)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{QuotationsUrl(setup.TenantId)}/{quotation.Id}")
        {
            Content = JsonContent.Create(SaveBody(quotation, parties)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        return await setup.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // El cuerpo tal como lo manda el frontend de antes del despliegue: con el interruptor billingWithRetention y sin
    // billingWithholdingTaxRateIds. Texto crudo para que ningún record agregue el campo nuevo.
    private static async Task<JsonElement> PutOldFrontendBodyAsync(Setup setup, QuotationResponse quotation)
    {
        var account = quotation.BillingAccount!;
        var items = string.Join(
            ",", quotation.Items.Select(item => $$"""{ "variantId": "{{item.VariantId}}", "quantity": {{item.Quantity.ToString(CultureInfo.InvariantCulture)}} }"""));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{QuotationsUrl(setup.TenantId)}/{quotation.Id}")
        {
            Content = new StringContent(
                $$"""
                {
                  "validUntil": "{{quotation.ValidUntil!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}",
                  "parties": {
                    "billing": {
                      "name": "Sede administrativa", "identificationNumber": "1020304050",
                      "phone": "3001234567", "email": "sede@verde.co", "address": "Calle 80 # 10-20",
                      "departmentId": "{{setup.City.DepartmentId}}", "cityId": "{{setup.City.CityId}}",
                      "personType": "Natural", "identificationType": "CC"
                    },
                    "shipping": null,
                    "billingWithRetention": true,
                    "billingVatSurplus": false
                  },
                  "billingAccount": {
                    "companyId": "{{account.CompanyId}}", "bankName": "{{account.BankName}}",
                    "accountNumber": "{{account.AccountNumber}}", "currency": "{{account.Currency}}"
                  },
                  "items": [{{items}}]
                }
                """,
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        var response = await setup.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<QuotationResponse> SendAsync(QepApiFactory factory, Setup setup, Guid quotationId)
    {
        var response = await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotationId}/send",
            new SendQuotationRequest(await CreateAvailablePdfFileAsync(setup.Client, factory, setup.TenantId)),
            TestContext.Current.CancellationToken);
        return await ReadAsync(response);
    }

    private static async Task<QuotationResponse> GetAsync(Setup setup, Guid quotationId) =>
        await ReadAsync(await setup.Client.GetAsync(
            $"{QuotationsUrl(setup.TenantId)}/{quotationId}", TestContext.Current.CancellationToken));

    private static async Task<QuotationResponse> ReadAsync(
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
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("code").GetString();
    }
}
