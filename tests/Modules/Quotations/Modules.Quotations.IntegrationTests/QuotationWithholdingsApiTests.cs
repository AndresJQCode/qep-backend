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

    // Spec ReteICA por empresa (A15): el cliente sólo tiene tasas nacionales, así que la base mínima en UVT se prueba con la
    // Retefuente. La ReteICA del documento sale de la empresa y del municipio (Bloque C).
    [Fact]
    public async Task ACustomerWithholdingGivesItsLine()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);

        var quotation = await CreateWithLinesAsync(setup, clientId, quantity: 3m);

        // Base = 300.000 (sin IVA): Retefuente 7.500 (300.000 ≥ 4 × 50.000).
        Assert.Equal(300_000m, quotation.Subtotal);
        Assert.Equal(
            new[]
            {
                (retefuente, "Retefuente", (decimal?)4m, (string?)"UVT", (decimal?)Uvt, 300_000m, 7_500m),
            },
            quotation.Withholdings
                .Select(row => (row.TaxRateId, row.Type, row.MinimumBase, row.MinimumBaseUnit, row.TaxUnitValue, row.Base, row.Amount))
                .ToArray());
        Assert.Equal(7_500m, quotation.RetentionAmount);
        Assert.Equal(quotation.Total - 7_500m, quotation.NetTotal);
        Assert.Equal(new[] { retefuente }, quotation.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Empty(quotation.BillingWithholdingTaxRates);
        // Con los datos del cliente no hay parte propia: null, como siempre.
        Assert.Null(quotation.BillingWithRetention);
    }

    // Spec ReteICA por empresa §7.5: la Retefuente del cliente y la ReteICA de la empresa en su municipio (el cliente es agente
    // ahí) dan dos líneas, y la retención es su suma.
    [Fact]
    public async Task TwoWithholdingsGiveTwoLinesAndTheirSum()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var reteIca = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m, minimumBase: 4m, cityId: setup.City.CityId);
        await AssignCompanyWithholdingRateAsync(factory, setup.TenantId, setup.Account.CompanyId, reteIca);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        await SetWithholdingAgentAsync(factory, setup.TenantId, clientId, setup.City.CityId);

        var quotation = await CreateWithLinesAsync(setup, clientId, quantity: 3m);

        // Base = 300.000: Retefuente 7.500 y ReteICA 2.898 (300.000 ≥ 4 × 50.000).
        Assert.Equal(
            new[] { (retefuente, 7_500m), (reteIca, 2_898m) },
            quotation.Withholdings.Select(row => (row.TaxRateId, row.Amount)).ToArray());
        Assert.Equal(10_398m, quotation.RetentionAmount);
        Assert.Equal(quotation.Total - 10_398m, quotation.NetTotal);
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

    // Spec 2026-11-18 servicios pendientes §2: las tasas congeladas del cliente y de la facturación propia viajan con su
    // alcance, tomado de la foto del documento, para que el selector diga «Retefuente (servicios)» sin el catálogo activo.
    [Fact]
    public async Task SavedRatesCarryTheirFrozenScope()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var compras = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente compras", "Retefuente", 2.5m, appliesTo: "Goods");
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios", "Retefuente", 4m, appliesTo: "Services");
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [compras]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        var saved = await ReadAsync(await PutAsync(setup, created, OwnBilling(setup, withholdingTaxRateIds: [servicios])));

        Assert.Equal("Goods", Assert.Single(saved.CustomerWithholdingTaxRates).AppliesTo);
        Assert.Equal("Services", Assert.Single(saved.BillingWithholdingTaxRates).AppliesTo);
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
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        const string identification = "900.111.222-5";
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, identification, withholdingTaxRateIds: [retefuente]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        await UpdateCustomerRetentionAsync(
            setup.Client, setup.TenantId, clientId, identification, withRetention: true, vatSurplus: true,
            withholdingTaxRateIds: [servicios]);
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
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        const string identification = "900.111.222-6";
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, identification, withholdingTaxRateIds: [retefuente]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        await UpdateCustomerRetentionAsync(
            setup.Client, setup.TenantId, clientId, identification, withRetention: false, vatSurplus: true,
            withholdingTaxRateIds: [servicios]);
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
        var servicios = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m);
        var firstClient = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        var secondClient = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var created = await CreateWithLinesAsync(setup, firstClient, quantity: 1m);

        var response = await setup.Client.PutAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{created.Id}/client",
            new { clientId = secondClient },
            TestContext.Current.CancellationToken);

        var changed = await ReadAsync(response);
        Assert.Equal(new[] { servicios }, changed.CustomerWithholdingTaxRates.Select(rate => rate.Id).ToArray());
        Assert.Equal(4_000m, changed.RetentionAmount);
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
        var withRetefuente = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        // El cuerpo dice withRetention true y se ignora: sólo una Retefuente lo prende. Desde el spec ReteICA por empresa
        // (A15) el cliente no puede tener una ReteICA, así que el caso sin Retefuente es un cliente sin tasas.
        var withoutRates = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withRetention: true, withholdingTaxRateIds: []);

        var first = await CreateQuotationAsync(setup.Client, setup.TenantId, withRetefuente);
        var second = await CreateQuotationAsync(setup.Client, setup.TenantId, withoutRates);

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

    // Spec retenciones por UVT §6: una tasa que no llega a la base mínima no desaparece; el desglose la trae con su
    // motivo, el umbral en dinero y la UVT con su año, y withholdings sigue siendo sólo las aplicadas.
    [Fact]
    public async Task BreakdownExplainsARateBelowTheMinimumBase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);

        var quotation = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        // Base 100.000 < 4 × 50.000.
        var entry = Assert.Single(quotation.WithholdingBreakdown);
        Assert.Equal(
            (servicios, false, "BelowMinimumBase", 100_000m, (decimal?)4m, (string?)"UVT", (decimal?)200_000m,
                (decimal?)Uvt, (int?)TodayInBogota().Year, false, 0m),
            (entry.TaxRateId, entry.Applies, entry.Reason, entry.Base, entry.MinimumBase, entry.MinimumBaseUnit,
                entry.MinimumBaseAmount, entry.TaxUnitValue, entry.TaxUnitYear, entry.TaxUnitOutdated, entry.Amount));
        Assert.Equal(
            ("Retefuente servicios 4 %", "Retefuente", "CO", 4m, 4m, false, (Guid?)null),
            (entry.Name, entry.Type, entry.Country, entry.Percentage, entry.CatalogPercentage, entry.PercentageOverridden,
                entry.CityId));
        Assert.Empty(quotation.Withholdings);
        Assert.Equal(0m, quotation.RetentionAmount);
    }

    // §5.2: un documento de este año con sólo la UVT del anterior cargada congela ésa y lo dice.
    [Fact]
    public async Task BreakdownFlagsAnOutdatedTaxUnit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var lastYear = TodayInBogota().Year - 1;
        var setup = await SetUpAsync(factory, taxUnitYear: lastYear);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);

        var quotation = await CreateWithLinesAsync(setup, clientId, quantity: 3m);

        var entry = Assert.Single(quotation.WithholdingBreakdown);
        Assert.Equal(
            (true, "Applied", (int?)lastYear, true, 12_000m),
            (entry.Applies, entry.Reason, entry.TaxUnitYear, entry.TaxUnitOutdated, entry.Amount));
        Assert.True(Assert.Single((await GetAsync(setup, quotation.Id)).WithholdingBreakdown).TaxUnitOutdated);
    }

    // Review Focus 2: creada el 1 de enero a la 01:00 UTC, que en Bogotá es todavía 31 de diciembre. La UVT de ese año
    // local no está desactualizada: el año del documento sale del calendario del tenant, no de UTC.
    [Fact]
    public async Task OutdatedUsesTheTenantLocalYear()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), utcNow: new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        var setup = await SetUpAsync(factory, taxUnitYear: 2025);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);

        var quotation = await CreateWithLinesAsync(setup, clientId, quantity: 1m);

        var entry = Assert.Single(quotation.WithholdingBreakdown);
        Assert.Equal(((int?)2025, false), (entry.TaxUnitYear, entry.TaxUnitOutdated));
    }

    // Spec retenciones por UVT §5.4 (owner P1): guardar una cotización editable, aunque sea sin cambios, le pone la UVT del
    // año que se cargó después y recalcula. Base 200.000: con 4 × 50.000 aplica (umbral inclusivo); con 4 × 60.000 no.
    [Fact]
    public async Task SavingRefreshesTheTaxUnit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var thisYear = TodayInBogota().Year;
        var (setup, created) = await OutdatedQuotationAsync(factory, thisYear - 1);
        await InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m);

        var saved = await ReadAsync(await PutAsync(setup, created, parties: null));

        AssertRefreshed(saved, thisYear);
        AssertRefreshed(await GetAsync(setup, created.Id), thisYear);
    }

    // §5.4: la vista previa refresca igual, pero no escribe: la cotización guardada sigue con la UVT del año anterior.
    [Fact]
    public async Task PreviewRefreshesWithoutSaving()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var thisYear = TodayInBogota().Year;
        var (setup, created) = await OutdatedQuotationAsync(factory, thisYear - 1);
        await InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m);

        var preview = await ReadAsync(await setup.Client.PostAsJsonAsync(
            $"{QuotationsUrl(setup.TenantId)}/{created.Id}/preview",
            SaveBody(created, parties: null),
            TestContext.Current.CancellationToken));

        AssertRefreshed(preview, thisYear);
        var fetched = await GetAsync(setup, created.Id);
        var entry = Assert.Single(fetched.WithholdingBreakdown);
        Assert.Equal(
            ((int?)(thisYear - 1), (decimal?)Uvt, true, "Applied", 8_000m),
            (entry.TaxUnitYear, entry.TaxUnitValue, entry.TaxUnitOutdated, entry.Reason, fetched.RetentionAmount));
        Assert.Equal(created.Version, fetched.Version);
    }

    // Una cotización con sólo la UVT del año anterior cargada, la Retefuente servicios con base de 4 UVT y 200.000 de base:
    // aplica justo en el umbral.
    private static async Task<(Setup Setup, QuotationResponse Created)> OutdatedQuotationAsync(QepApiFactory factory, int year)
    {
        var setup = await SetUpAsync(factory, taxUnitYear: year);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var created = await CreateWithLinesAsync(setup, clientId, quantity: 2m);
        var entry = Assert.Single(created.WithholdingBreakdown);
        Assert.Equal(((int?)year, true, 8_000m), (entry.TaxUnitYear, entry.TaxUnitOutdated, created.RetentionAmount));
        return (setup, created);
    }

    private static void AssertRefreshed(QuotationResponse quotation, int year)
    {
        var entry = Assert.Single(quotation.WithholdingBreakdown);
        Assert.Equal(
            ((int?)year, (decimal?)60_000m, false, "BelowMinimumBase", (decimal?)240_000m, 0m),
            (entry.TaxUnitYear, entry.TaxUnitValue, entry.TaxUnitOutdated, entry.Reason, entry.MinimumBaseAmount, entry.Amount));
        Assert.Empty(quotation.Withholdings);
        Assert.Equal((0m, quotation.Total), (quotation.RetentionAmount, quotation.NetTotal));
    }

    private sealed record Setup(
        HttpClient Client,
        Guid TenantId,
        Guid ProductId,
        Guid VatRateId,
        QuotationBillingAccountRequest Account,
        CityInDepartment City);

    // Tenant, empresa colombiana con cuenta en COP, UVT del año del documento (u otro, con taxUnitYear) y un producto de
    // 119.000 con IVA del 19 % adentro (base 100.000 por unidad).
    private static async Task<Setup> SetUpAsync(QepApiFactory factory, int? taxUnitYear = null)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        await InsertTaxUnitValueAsync(factory, "CO", taxUnitYear ?? TodayInBogota().Year, Uvt);
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
                setup.City.DepartmentId, setup.City.CityId, PersonType: "Natural", IdentificationType: "CC", Country: "CO"),
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
                      "personType": "Natural", "identificationType": "CC", "country": "CO"
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
