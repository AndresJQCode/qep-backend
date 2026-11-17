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
        // Spec ReteICA por empresa (A15): el cliente ya no puede tener una ReteICA; la del pedido sale de la empresa y del
        // municipio (Bloque C).
        var clientId = await CreateActiveCustomerAsync(
            setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);

        var order = await ConvertAsync(setup, quotation.Id);

        // La misma foto: la edición la prellena (D21).
        Assert.Equal(
            quotation.Withholdings.Select(row => (row.TaxRateId, row.Base, row.Amount, row.TaxUnitValue)).ToArray(),
            order.Withholdings.Select(row => (row.TaxRateId, row.Base, row.Amount, row.TaxUnitValue)).ToArray());
        Assert.Equal((retefuente, 2_500m), (Assert.Single(order.Withholdings).TaxRateId, order.RetentionAmount));
        Assert.Equal(new[] { retefuente }, order.BillingWithholdingTaxRates.Select(rate => rate.Id).ToArray());
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

    // D17/D21: las tasas del pedido quedaron congeladas. Reenviar las mismas (lo que hace todo guardado) no vuelve a leer
    // el catálogo: una tasa desactivada después no hace 422 cada guardado ni cambia la foto.
    [Fact]
    public async Task ResendingTheSameRatesAfterDeactivatingOneKeepsTheFrozenPhoto()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [retefuente]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        var order = await ConvertAsync(setup, quotation.Id);
        await ChangeTaxRateAsync(factory, retefuente, percentage: 3m, isActive: false);

        var response = await PutEditsAsync(setup, order, Edits(setup, withholdingTaxRateIds: [retefuente]));

        var saved = await ReadDetailAsync(response);
        var applied = Assert.Single(saved.Order.Withholdings);
        Assert.Equal((retefuente, 2.5m, 2_500m), (applied.TaxRateId, applied.Percentage, applied.Amount));
        Assert.Equal(2_500m, saved.Order.RetentionAmount);
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
        // Spec ReteICA por empresa §7.5: la ReteICA es la de la empresa colombiana en su municipio. La edición cambia de
        // empresa, así que el selector la acepta aunque el cliente no sea agente ahí (R11, A11).
        var reteIca = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "ReteICA Bogotá", "ReteICA", 0.966m, minimumBase: 4m,
            cityId: await EnsureCityIdAsync(setup.Client));
        await AssignCompanyWithholdingRateAsync(factory, setup.TenantId, setup.Account.CompanyId, reteIca);
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
    // Un pedido preparado sigue abierto, pero ya no es editable (Order.IsEditable, spec 2026-11-10 §3.6): los selectores y
    // la empresa siguen la misma regla que el resto de la edición.
    public async Task APreparedOrderRejectsWithholdingAndAccountEdits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory, ApproverPermissions);
        var retefuente = await InsertWithholdingRateAsync(factory, setup.TenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var created = await CreateManualAsync(setup, clientId, setup.Account);
        var prepare = await setup.Client.PostAsync(
            $"{OrdersUrl(setup.TenantId)}/{created.Id}/prepare", null, TestContext.Current.CancellationToken);
        prepare.EnsureSuccessStatusCode();
        var prepared = await prepare.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("Open", prepared!.Status);

        var response = await PutEditsAsync(
            setup, prepared, Edits(setup, withholdingTaxRateIds: [retefuente], billingAccount: setup.Account));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_editable", await CodeOfAsync(response));
        Assert.Empty((await GetDetailAsync(setup, created.Id)).Order.BillingWithholdingTaxRates);
    }

    // Spec retenciones por UVT §6: el pedido convertido hereda la foto, así que su desglose es el de la cotización.
    [Fact]
    public async Task ConvertedOrderCarriesTheBreakdown()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 4m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);

        var order = await ConvertAsync(setup, quotation.Id);

        // Base 100.000 < 4 × 50.000.
        var entry = Assert.Single(order.WithholdingBreakdown);
        Assert.Equal(
            (servicios, false, "BelowMinimumBase", 100_000m, (decimal?)200_000m, (int?)TodayInBogota().Year, false, 0m),
            (entry.TaxRateId, entry.Applies, entry.Reason, entry.Base, entry.MinimumBaseAmount, entry.TaxUnitYear,
                entry.TaxUnitOutdated, entry.Amount));
        Assert.Equal(quotation.WithholdingBreakdown, order.WithholdingBreakdown);
        Assert.Empty(order.Withholdings);
    }

    // Spec retenciones por UVT §5.4 (owner P1): un pedido editable que se guarda, aunque sea sin cambios, toma la UVT del
    // año cargada después y recalcula. Base 100.000: con 2 × 50.000 aplica; con 2 × 60.000 ya no.
    [Fact]
    public async Task SavingAnEditableOrderRefreshesTheTaxUnit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var thisYear = TodayInBogota().Year;
        var setup = await SetUpAsync(factory, taxUnitYear: thisYear - 1);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 2m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        var order = await ConvertAsync(setup, quotation.Id);
        Assert.Equal((4_000m, (int?)(thisYear - 1)), (order.RetentionAmount, Assert.Single(order.WithholdingBreakdown).TaxUnitYear));
        await InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m);

        var saved = await ReadDetailAsync(await PutEditsAsync(setup, order, Edits(setup)));

        var entry = Assert.Single(saved.Order.WithholdingBreakdown);
        Assert.Equal(
            ((int?)thisYear, (decimal?)60_000m, false, "BelowMinimumBase", (decimal?)120_000m),
            (entry.TaxUnitYear, entry.TaxUnitValue, entry.TaxUnitOutdated, entry.Reason, entry.MinimumBaseAmount));
        Assert.Equal((0m, saved.Order.Total), (saved.Order.RetentionAmount, saved.Order.NetTotal));
        Assert.Empty(saved.Order.Withholdings);
        var fetched = await GetDetailAsync(setup, order.Id);
        Assert.Equal((int?)thisYear, Assert.Single(fetched.Order.WithholdingBreakdown).TaxUnitYear);
        Assert.Equal(0m, fetched.Order.RetentionAmount);
    }

    // Spec alerta UVT §5.2 (owner A): la conversión refresca la UVT al año del pedido. La cotización se envió con la del año
    // anterior (2 × 50.000 = 100.000, aplica: 4.000); con la del año cargada antes de convertir (2 × 60.000) ya no aplica.
    [Fact]
    public async Task ConvertingRefreshesTheTaxUnitToTheOrderYear()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var thisYear = TodayInBogota().Year;
        var setup = await SetUpAsync(factory, taxUnitYear: thisYear - 1);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 2m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        Assert.Equal(4_000m, quotation.RetentionAmount);
        await InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m);

        var order = await ConvertAsync(setup, quotation.Id);

        var entry = Assert.Single(order.WithholdingBreakdown);
        Assert.Equal(
            ((int?)thisYear, (decimal?)60_000m, false, "BelowMinimumBase", (decimal?)120_000m),
            (entry.TaxUnitYear, entry.TaxUnitValue, entry.TaxUnitOutdated, entry.Reason, entry.MinimumBaseAmount));
        Assert.Equal((0m, order.Total), (order.RetentionAmount, order.NetTotal));
    }

    // Spec alerta UVT §5.4: el pedido se convirtió con la UVT del año anterior (la única cargada); cargada la del año, el
    // detalle dice que está desactualizada y cuál tomaría al guardarse, sin que el frontend la deduzca.
    [Fact]
    public async Task DetailCarriesTheLatestTaxUnitYear()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var thisYear = TodayInBogota().Year;
        var setup = await SetUpAsync(factory, taxUnitYear: thisYear - 1);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 2m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        var order = await ConvertAsync(setup, quotation.Id);
        await InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m);

        var detail = await GetDetailAsync(setup, order.Id);

        var entry = Assert.Single(detail.Order.WithholdingBreakdown);
        Assert.Equal(
            ((int?)(thisYear - 1), true, (int?)thisYear),
            (entry.TaxUnitYear, entry.TaxUnitOutdated, entry.TaxUnitLatestYear));
    }

    // Spec alerta UVT §5.3: la cotización se envió con la UVT del año anterior (2 × 50.000 = 100.000, aplica: 4.000); cargada
    // la del año (2 × 60.000), la vista previa dice qué cambia al convertir, con los dos lados calculados en el backend.
    [Fact]
    public async Task ConversionPreviewShowsTheTaxUnitChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var thisYear = TodayInBogota().Year;
        var setup = await SetUpAsync(factory, taxUnitYear: thisYear - 1);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 2m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        await InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m);

        var preview = await GetConversionPreviewAsync(setup, quotation.Id);

        Assert.NotNull(preview.TaxUnitChange);
        Assert.Equal(
            [new TaxUnitShiftDto("CO", thisYear - 1, Uvt, thisYear, 60_000m)], preview.TaxUnitChange.TaxUnits);
        var row = Assert.Single(preview.TaxUnitChange.Withholdings);
        Assert.Equal(
            (servicios, true, false, 4_000m, 0m, (decimal?)100_000m, (decimal?)120_000m),
            (row.TaxRateId, row.FromApplies, row.ToApplies, row.FromAmount, row.ToAmount, row.FromMinimumBaseAmount,
                row.ToMinimumBaseAmount));
        Assert.Equal(
            (quotation.NetTotal, quotation.Total, quotation.Total, 0m),
            (preview.TaxUnitChange.FromNetTotal, preview.TaxUnitChange.ToNetTotal, preview.NetTotal, preview.RetentionAmount));
        var entry = Assert.Single(preview.WithholdingBreakdown);
        Assert.Equal(
            ((int?)thisYear, false, (int?)thisYear, "BelowMinimumBase"),
            (entry.TaxUnitYear, entry.TaxUnitOutdated, entry.TaxUnitLatestYear, entry.Reason));
    }

    // Con la UVT del año ya cargada al cotizar, convertir no cambia nada: no hay alerta.
    [Fact]
    public async Task ConversionPreviewWithoutChangeHasNoTaxUnitChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 2m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);

        var preview = await GetConversionPreviewAsync(setup, quotation.Id);

        Assert.Null(preview.TaxUnitChange);
        Assert.Equal((quotation.NetTotal, quotation.RetentionAmount), (preview.NetTotal, preview.RetentionAmount));
    }

    // Los números de pedido no tienen huecos: la vista previa no toma el contador, así que el primer pedido del tenant sigue
    // siendo el 0001 (el formato por defecto de DocumentNumberingApiTests).
    [Fact]
    public async Task ConversionPreviewDoesNotSpendTheOrderNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 2m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);

        await GetConversionPreviewAsync(setup, quotation.Id);
        await GetConversionPreviewAsync(setup, quotation.Id);
        var order = await ConvertAsync(setup, quotation.Id);

        Assert.Equal($"PED-{TodayInBogota().Year}-0001", order.OrderNumber);
    }

    // Las mismas precondiciones que la conversión antes de pedir el número: una cotización convertida no se convierte otra vez.
    [Fact]
    public async Task ConversionPreviewOfAConvertedQuotationIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        await ConvertAsync(setup, quotation.Id);

        var response = await setup.Client.GetAsync(ConversionPreviewUrl(setup, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.quotation.status_not_convertible", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ConversionPreviewRequiresOrderManage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, OrdersPermissions.OrderRead);
        using var _ = client;

        var response = await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{Guid.CreateVersion7()}/order/preview", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Review Focus 3: un pedido facturado ya no es editable; cargar la UVT del año no lo mueve, ni al leerlo ni al
    // intentar editarlo.
    [Fact]
    public async Task InvoicedOrderKeepsItsTaxUnit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var thisYear = TodayInBogota().Year;
        var setup = await SetUpAsync(
            factory, [.. ApproverPermissions, OrdersPermissions.OrderInvoice], taxUnitYear: thisYear - 1);
        var servicios = await InsertWithholdingRateAsync(
            factory, setup.TenantId, "Retefuente servicios 4 %", "Retefuente", 4m, minimumBase: 2m);
        var clientId = await CreateActiveCustomerAsync(setup.Client, setup.TenantId, withholdingTaxRateIds: [servicios]);
        var quotation = await CreateSentQuotationAsync(setup.Client, factory, setup.TenantId, clientId, setup.ProductId);
        var order = await ConvertAsync(setup, quotation.Id);
        (await setup.Client.PostAsync(
            $"{OrdersUrl(setup.TenantId)}/{order.Id}/mark-paid", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        (await setup.Client.PostAsync(
            $"{OrdersUrl(setup.TenantId)}/{order.Id}/invoice", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        await InsertTaxUnitValueAsync(factory, "CO", thisYear, 60_000m);

        var invoiced = await GetDetailAsync(setup, order.Id);
        var rejected = await PutEditsAsync(setup, invoiced.Order, Edits(setup));

        Assert.Equal("order.order.not_editable", await CodeOfAsync(rejected));
        var fetched = await GetDetailAsync(setup, order.Id);
        var entry = Assert.Single(fetched.Order.WithholdingBreakdown);
        Assert.Equal(
            ((int?)(thisYear - 1), (decimal?)Uvt, true, "Applied"),
            (entry.TaxUnitYear, entry.TaxUnitValue, entry.TaxUnitOutdated, entry.Reason));
        Assert.Equal(4_000m, fetched.Order.RetentionAmount);
    }

    private sealed record Setup(
        HttpClient Client,
        Guid TenantId,
        Guid ProductId,
        QuotationBillingAccountRequest Account);

    // Tenant, empresa colombiana con cuenta en COP, UVT del año del documento y un producto de 119.000 con IVA del 19 %
    // adentro (base 100.000 por unidad).
    private static async Task<Setup> SetUpAsync(QepApiFactory factory, string[]? permissions = null, int? taxUnitYear = null)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, permissions ?? ManagerPermissions);
        await InsertTaxUnitValueAsync(factory, "CO", taxUnitYear ?? TodayInBogota().Year, Uvt);
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
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var converted = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(converted);
        return (await GetDetailAsync(setup, converted.Id)).Order;
    }

    private static string ConversionPreviewUrl(Setup setup, Guid quotationId) =>
        $"{QuotationsUrl(setup.TenantId)}/{quotationId}/order/preview";

    private static async Task<QuotationConversionPreviewResponse> GetConversionPreviewAsync(Setup setup, Guid quotationId)
    {
        var response = await setup.Client.GetAsync(ConversionPreviewUrl(setup, quotationId), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {text}");
        var preview = JsonSerializer.Deserialize<QuotationConversionPreviewResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(preview);
        return preview;
    }

    private static async Task<OrderResponse> CreateManualAsync(
        Setup setup, Guid clientId, QuotationBillingAccountRequest account, decimal quantity = 1m)
    {
        var response = await setup.Client.PostAsJsonAsync(
            OrdersUrl(setup.TenantId),
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null, account,
                [new OrderEditItemRequest(setup.ProductId, quantity)], []),
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
