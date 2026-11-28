using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec ReteICA por empresa §7.5–§7.8, §9.4 en la cotización: la ReteICA sale de la empresa facturadora y del municipio de la
/// operación, se congela con su municipio y su porcentaje del catálogo, se explica al leer (<c>municipalWithholdings</c>) y el
/// asesor la puede editar, elegir a mano con facturación propia o marcar «Sin retención». Las tasas, las asignaciones de la
/// empresa y los agentes del cliente se siembran por SQL (<see cref="QuotationsApiHarness.InsertWithholdingRateAsync"/>).
///
/// Fixture: empresa CO en la primera ciudad sembrada («Bogotá» en los nombres) con «ReteICA Bogotá» 0,966 % asignada, y un
/// producto de 119.000 con IVA del 19 % adentro (base 100.000). Sin base mínima: la UVT es global y la base es compartida.
/// </summary>
public sealed class QuotationMunicipalWithholdingsApiTests(SharedQuotationsHost sharedHost)
    : IClassFixture<SharedQuotationsHost>
{
    [Fact]
    public async Task CompanyInBogotaAndAgentCustomerAppliesReteIca()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);

        var created = await CreateAsync(fx, clientId);

        var row = Assert.Single(created.Withholdings);
        Assert.Equal(
            (fx.ReteIcaBogota, (Guid?)fx.Bogota.CityId, 0.966m, 0.966m, false, 966m),
            (row.TaxRateId, row.CityId, row.Percentage, row.CatalogPercentage, row.PercentageOverridden, row.Amount));
        Assert.Equal(fx.Bogota.CityId, created.OperationCity?.Id);
        Assert.Equal(fx.Bogota.DepartmentId, created.OperationCity?.DepartmentId);
        Assert.Equal(fx.Bogota.CityId, created.BillingCompanyCity?.Id);
        var entry = Assert.Single(created.MunicipalWithholdings);
        Assert.Equal(
            ("ReteICA", "CO", fx.ReteIcaBogota, "ReteICA Bogotá", 0.966m, "Resolved", (bool?)true),
            (entry.Type, entry.Country, entry.CompanyRate?.TaxRateId, entry.CompanyRate?.Name, entry.CompanyRate?.Percentage,
                entry.Status, entry.CustomerIsAgent));
        Assert.Equal(fx.ReteIcaBogota, Assert.Single(created.CustomerWithholdingTaxRates).Id);
    }

    [Fact]
    public async Task OperationCityWithoutCompanyRateIsNoCompanyRate()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);

        var saved = await ReadAsync(await PutAsync(fx, created, Body(created) with { OperationCityId = Json(fx.Medellin.CityId) }));

        Assert.Empty(saved.Withholdings);
        Assert.Equal(0m, saved.RetentionAmount);
        Assert.Equal(fx.Medellin.CityId, saved.OperationCity?.Id);
        Assert.Equal(fx.Bogota.CityId, saved.BillingCompanyCity?.Id);
        var entry = Assert.Single(saved.MunicipalWithholdings);
        Assert.Equal(("NoCompanyRate", (Guid?)null), (entry.Status, entry.CompanyRate?.TaxRateId));
        Assert.Contains(
            await HistoryAsync(fx, saved.Id),
            item => item.EventType == "Edited" && item.Details!.Contains("Cambió el municipio de la operación", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EditingThePercentageRecalculatesAndLogs()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);

        var saved = await ReadAsync(await PutAsync(fx, created, Body(created) with { WithholdingPercentages = [new("ReteICA", 0.8m)] }));

        var row = Assert.Single(saved.Withholdings);
        Assert.Equal((0.8m, 0.966m, true, 800m), (row.Percentage, row.CatalogPercentage, row.PercentageOverridden, row.Amount));
        Assert.Equal(800m, saved.RetentionAmount);
        Assert.Equal(created.NetTotal + 166m, saved.NetTotal);
        Assert.Equal(
            ["Cambió la tarifa de ReteICA Bogotá de 0,966 % a 0,8 %."],
            await RateEditedAsync(fx, saved.Id));
        Assert.Contains(
            "quotation.quotation.withholding_rate_edited",
            (await OutboxMessagesAsync(fx.Factory, "platform.audit.recorded.v1")).Select(ActionOf));

        // Volver al del catálogo deja su propia fila.
        var restored = await ReadAsync(await PutAsync(fx, saved, Body(saved) with { WithholdingPercentages = [new("ReteICA", null)] }));
        Assert.Equal(0.966m, Assert.Single(restored.Withholdings).Percentage);
        Assert.Equal("Restableció la tarifa de ReteICA Bogotá a 0,966 %.", (await RateEditedAsync(fx, saved.Id))[^1]);
    }

    // Review Focus 1: el frontend reenvía el selector que tiene en pantalla en cada guardado; la foto editada se queda.
    [Fact]
    public async Task ResendingTheSameMunicipalSelectorKeepsTheEditedPercentage()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);
        var edited = await ReadAsync(await PutAsync(
            fx, created, Body(created, OwnBilling(fx, [fx.ReteIcaBogota])) with { WithholdingPercentages = [new("ReteICA", 0.8m)] }));
        Assert.Equal(0.8m, Assert.Single(edited.Withholdings).Percentage);

        var resent = await ReadAsync(await PutAsync(fx, edited, Body(edited, OwnBilling(fx, [fx.ReteIcaBogota]))));

        var row = Assert.Single(resent.Withholdings);
        Assert.Equal((0.8m, true, 800m), (row.Percentage, row.PercentageOverridden, row.Amount));
        Assert.Equal(0.8m, Assert.Single(resent.BillingWithholdingTaxRates).Percentage);
        Assert.Single(await RateEditedAsync(fx, created.Id));
    }

    // A18 (owner 2026-11-14): la empresa reemplaza su tasa de Bogotá y la cotización se vuelve a guardar con sólo las notas
    // cambiadas y la lista completa de ids, la guardada incluida. Va en otro orden que el guardado para que el selector se
    // vuelva a resolver: el id guardado se acepta y su fila se queda como estaba.
    [Fact]
    public async Task ReplacedCompanyRateKeepsTheStoredIdOnANotesOnlySave()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);
        var stored = await ReadAsync(await PutAsync(fx, created, Body(created, OwnBilling(fx, [fx.ReteIcaBogota, fx.Retefuente]))));
        var replacement = await ReplaceBogotaRateAsync(fx);

        var saved = await ReadAsync(await PutAsync(
            fx, stored, Body(stored, OwnBilling(fx, [fx.Retefuente, fx.ReteIcaBogota])) with { Notes = "Sólo cambian las notas" }));

        var reteIca = Assert.Single(saved.Withholdings, row => row.Type == "ReteICA");
        Assert.Equal((fx.ReteIcaBogota, 0.966m, false), (reteIca.TaxRateId, reteIca.Percentage, reteIca.PercentageOverridden));
        Assert.Equal(replacement, Assert.Single(saved.MunicipalWithholdings).CompanyRate?.TaxRateId);
        Assert.Empty(await RateEditedAsync(fx, saved.Id));
    }

    // A18: cambiar sólo un selector nacional no relee el catálogo para la fila municipal guardada; el 0,8 % sobrevive.
    [Fact]
    public async Task ChangingANationalSelectorKeepsTheEditedMunicipalPercentage()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);
        var edited = await ReadAsync(await PutAsync(
            fx, created, Body(created, OwnBilling(fx, [fx.ReteIcaBogota])) with { WithholdingPercentages = [new("ReteICA", 0.8m)] }));

        var body = Body(edited, OwnBilling(fx, [fx.ReteIcaBogota, fx.Retefuente]));
        var preview = await ReadAsync(await PostPreviewAsync(fx, edited, body));
        var saved = await ReadAsync(await PutAsync(fx, edited, body));

        foreach (var quotation in new[] { preview, saved })
        {
            var reteIca = Assert.Single(quotation.Withholdings, row => row.Type == "ReteICA");
            Assert.Equal((0.8m, 0.966m, true), (reteIca.Percentage, reteIca.CatalogPercentage, reteIca.PercentageOverridden));
            Assert.Contains(quotation.Withholdings, row => row.TaxRateId == fx.Retefuente);
        }

        Assert.Single(await RateEditedAsync(fx, saved.Id));
    }

    // Control de A18: un id municipal que no es ni el guardado ni el de la empresa sigue siendo 422.
    [Fact]
    public async Task AMunicipalIdNeitherStoredNorOfferedIs422()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);
        var stored = await ReadAsync(await PutAsync(fx, created, Body(created, OwnBilling(fx, [fx.ReteIcaBogota]))));
        var unassigned = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Bogotá sin asignar", "ReteICA", 1.2m, cityId: fx.Bogota.CityId);

        var response = await PutAsync(fx, stored, Body(stored, OwnBilling(fx, [unassigned, fx.Retefuente])));

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", await CodeAsync(response));
    }

    [Fact]
    public async Task InvalidBodiesAre422()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);

        var retefuente = await PutAsync(fx, created, Body(created) with { WithholdingPercentages = [new("Retefuente", 1m)] });
        var fourDecimals = await PutAsync(fx, created, Body(created) with { WithholdingPercentages = [new("ReteICA", 0.8125m)] });
        var unknownCity = await PutAsync(fx, created, Body(created) with { OperationCityId = Json(Guid.NewGuid()) });
        var notAGuid = await PutAsync(fx, created, Body(created) with { OperationCityId = JsonSerializer.SerializeToElement("bogota") });

        Assert.Equal("quotation.billing.withholding_percentage_not_editable", await CodeAsync(retefuente));
        Assert.Equal("quotation.billing.withholding_percentage_invalid", await CodeAsync(fourDecimals));
        Assert.Equal("quotation.billing.operation_city_invalid", await CodeAsync(unknownCity));
        Assert.Equal("quotation.billing.operation_city_invalid", await CodeAsync(notAGuid));
    }

    [Fact]
    public async Task PreviewsReturnOperationCityAndMunicipalWithholdings()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);

        var newPreview = await ReadAsync(await fx.Client.PostAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/preview", CreateBody(fx, clientId), TestContext.Current.CancellationToken));
        var editPreview = await ReadAsync(await fx.Client.PostAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/{created.Id}/preview",
            Body(created) with { WithholdingPercentages = [new("ReteICA", 0.8m)] },
            TestContext.Current.CancellationToken));

        Assert.Equal(fx.Bogota.CityId, newPreview.OperationCity?.Id);
        Assert.Equal("Resolved", Assert.Single(newPreview.MunicipalWithholdings).Status);
        Assert.Equal(0.8m, Assert.Single(editPreview.Withholdings).Percentage);
        Assert.Equal(fx.Bogota.CityId, editPreview.OperationCity?.Id);
        Assert.Empty(await RateEditedAsync(fx, created.Id));
        Assert.Equal(0.966m, Assert.Single((await GetAsync(fx, created.Id)).Withholdings).Percentage);
    }

    // R11: con facturación propia el asesor aplica a mano la tasa de la empresa aunque el cliente no sea agente.
    [Fact]
    public async Task OwnBillingCanApplyTheCompanysRateByHand()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var created = await CreateAsync(fx, clientId);
        Assert.Equal("CustomerNotAgent", Assert.Single(created.MunicipalWithholdings).Status);

        var saved = await ReadAsync(await PutAsync(fx, created, Body(created, OwnBilling(fx, [fx.ReteIcaBogota]))));

        Assert.Equal(fx.ReteIcaBogota, Assert.Single(saved.Withholdings).TaxRateId);
        var entry = Assert.Single(saved.MunicipalWithholdings);
        Assert.Equal(("Resolved", (bool?)null), (entry.Status, entry.CustomerIsAgent));
        Assert.Contains(
            await HistoryAsync(fx, saved.Id),
            item => item.EventType == "Edited" && item.Details!.Contains("Aplicó ReteICA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnBillingSelectorOfAnotherCityIs422()
    {
        var fx = await SetUpAsync();
        var medellinRate = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Medellín", "ReteICA", 1m, cityId: fx.Medellin.CityId);
        await AssignCompanyWithholdingRateAsync(fx.Factory, fx.TenantId, fx.BogotaAccount.CompanyId, medellinRate);
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);

        var response = await PutAsync(fx, created, Body(created, OwnBilling(fx, [medellinRate])));

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", await CodeAsync(response));
    }

    // A11 (owner 2026-11-13): el cambio de empresa re-resuelve y, en el mismo cuerpo, acepta la tasa de la empresa nueva y su
    // porcentaje. El historial anota la tarifa descartada y después la editada.
    [Fact]
    public async Task CompanyChangeAcceptsTheNewCompanysRate()
    {
        var fx = await SetUpAsync();
        var a11 = await A11StartAsync(fx);
        var body = Body(a11.Edited, OwnBilling(fx, [a11.MedellinRate, fx.Retefuente]), a11.MedellinAccount) with
        {
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        };

        var preview = await ReadAsync(await PostPreviewAsync(fx, a11.Edited, body));
        var saved = await ReadAsync(await PutAsync(fx, a11.Edited, body));

        foreach (var quotation in new[] { preview, saved })
        {
            var reteIca = Assert.Single(quotation.Withholdings, row => row.Type == "ReteICA");
            Assert.Equal((a11.MedellinRate, 0.7m, true), (reteIca.TaxRateId, reteIca.Percentage, reteIca.PercentageOverridden));
            Assert.Contains(quotation.Withholdings, row => row.TaxRateId == fx.Retefuente);
            Assert.Equal(fx.Medellin.CityId, quotation.OperationCity?.Id);
        }

        Assert.Equal(
            [
                "Cambió la tarifa de ReteICA Bogotá de 0,966 % a 0,8 %.",
                "Se volvió a calcular la ReteICA por cambio de municipio o de empresa; se descartó la tarifa editada (0,8 %).",
                "Cambió la tarifa de ReteICA Medellín de 1 % a 0,7 %.",
            ],
            await RateEditedAsync(fx, a11.Edited.Id));
    }

    [Fact]
    public async Task CompanyChangeIgnoresTheOldCompanysRate()
    {
        var fx = await SetUpAsync();
        var a11 = await A11StartAsync(fx);
        var body = Body(a11.Edited, OwnBilling(fx, [fx.ReteIcaBogota, fx.Retefuente]), a11.MedellinAccount) with
        {
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        };

        var preview = await ReadAsync(await PostPreviewAsync(fx, a11.Edited, body));
        var saved = await ReadAsync(await PutAsync(fx, a11.Edited, body));

        foreach (var quotation in new[] { preview, saved })
        {
            var reteIca = Assert.Single(quotation.Withholdings, row => row.Type == "ReteICA");
            Assert.Equal((a11.MedellinRate, 0.7m), (reteIca.TaxRateId, reteIca.Percentage));
            Assert.Contains(quotation.Withholdings, row => row.TaxRateId == fx.Retefuente);
        }

        // La empresa nueva no tiene tasa en el municipio: la re-resolución no deja fila y el porcentaje se ignora sin 422.
        var bare = await CreateCompanyWithBankAccountAsync(fx.Client, fx.TenantId, cityId: fx.Medellin.CityId);
        var bareAccount = new QuotationBillingAccountRequest(bare.CompanyId, bare.BankName, bare.AccountNumber, bare.Currency);
        var current = await GetAsync(fx, a11.Edited.Id);
        var noRow = Body(current, OwnBilling(fx, [a11.MedellinRate]), bareAccount) with
        {
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        };
        var noRowPreview = await ReadAsync(await PostPreviewAsync(fx, current, noRow));
        var noRowSaved = await ReadAsync(await PutAsync(fx, current, noRow));
        Assert.DoesNotContain(noRowPreview.Withholdings, row => row.Type == "ReteICA");
        Assert.DoesNotContain(noRowSaved.Withholdings, row => row.Type == "ReteICA");
        Assert.Equal("NoCompanyRate", Assert.Single(noRowSaved.MunicipalWithholdings).Status);
    }

    // «Sin retención» (owner 2026-11-13) en el mismo cuerpo que cambia la empresa: quita la fila que dejó la re-resolución.
    [Fact]
    public async Task CompanyChangeWithClearedReteIcaSavesNoRow()
    {
        var fx = await SetUpAsync();
        var a11 = await A11StartAsync(fx);
        var body = Body(a11.Edited, OwnBilling(fx, [fx.Retefuente]), a11.MedellinAccount) with
        {
            ClearedWithholdingTypes = ["ReteICA"],
        };

        var preview = await ReadAsync(await PostPreviewAsync(fx, a11.Edited, body));
        Assert.Single(await RateEditedAsync(fx, a11.Edited.Id));
        var saved = await ReadAsync(await PutAsync(fx, a11.Edited, body));

        foreach (var quotation in new[] { preview, saved })
        {
            Assert.Equal(fx.Retefuente, Assert.Single(quotation.Withholdings).TaxRateId);
            Assert.DoesNotContain(quotation.BillingWithholdingTaxRates, rate => rate.Type == "ReteICA");
            Assert.Equal("NotSelected", Assert.Single(quotation.MunicipalWithholdings).Status);
        }

        Assert.Single(
            await RateEditedAsync(fx, a11.Edited.Id),
            text => text.Contains("se descartó la tarifa editada (0,8 %)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearedAndSelectedIsConflict()
    {
        var fx = await SetUpAsync();
        var a11 = await A11StartAsync(fx);

        var withId = await PutAsync(fx, a11.Edited, Body(a11.Edited, OwnBilling(fx, [a11.MedellinRate]), a11.MedellinAccount) with
        {
            ClearedWithholdingTypes = ["ReteICA"],
        });
        var withPercentage = await PutAsync(fx, a11.Edited, Body(a11.Edited, OwnBilling(fx, [fx.Retefuente]), a11.MedellinAccount) with
        {
            ClearedWithholdingTypes = ["ReteICA"],
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        });

        Assert.Equal("quotation.billing.withholding_selection_conflict", await CodeAsync(withId));
        Assert.Equal("quotation.billing.withholding_selection_conflict", await CodeAsync(withPercentage));
        Assert.Equal(a11.Edited.Version, (await GetAsync(fx, a11.Edited.Id)).Version);
    }

    [Fact]
    public async Task ClearedNonMunicipalTypeIs422()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, clientId);

        var response = await PutAsync(fx, created, Body(created) with { ClearedWithholdingTypes = ["Retefuente"] });

        Assert.Equal("quotation.billing.withholding_type_invalid", await CodeAsync(response));
    }

    [Fact]
    public async Task ClearedWithoutContextChangeRemovesTheRow()
    {
        var fx = await SetUpAsync();
        var a11 = await A11StartAsync(fx);

        // Como la SPA: «Sin retención» viaja como marca y el selector sin el id de ReteICA.
        var saved = await ReadAsync(await PutAsync(fx, a11.Edited, Body(a11.Edited, OwnBilling(fx, [])) with
        {
            ClearedWithholdingTypes = ["ReteICA"],
        }));

        Assert.Empty(saved.Withholdings);
        Assert.Equal(0m, saved.RetentionAmount);
        Assert.Equal(
            "Se volvió a calcular la ReteICA por cambio de municipio o de empresa; se descartó la tarifa editada (0,8 %).",
            (await RateEditedAsync(fx, saved.Id))[^1]);
        Assert.Contains(
            await HistoryAsync(fx, saved.Id),
            item => item.EventType == "Edited" && item.Details!.Contains("retenciones de facturación", StringComparison.Ordinal));
    }

    // Creación con ReteICA (owner 2026-11-14, P13, Review Focus 8): cada caso contra POST /quotations y su vista previa.
    [Fact]
    public async Task NewQuotePreviewOffersTheCompanyRate()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);

        var preview = await ReadAsync(await fx.Client.PostAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/preview", CreateBody(fx, clientId, OwnBilling(fx, [])),
            TestContext.Current.CancellationToken));

        Assert.Equal((Guid.Empty, fx.Bogota.CityId, fx.Bogota.CityId), (preview.Id, preview.OperationCity?.Id, preview.BillingCompanyCity?.Id));
        var entry = Assert.Single(preview.MunicipalWithholdings);
        Assert.Equal((fx.ReteIcaBogota, "NotSelected", (bool?)null), (entry.CompanyRate?.TaxRateId, entry.Status, entry.CustomerIsAgent));
    }

    [Fact]
    public async Task CreateWithTheCompanysReteIcaAppliesIt()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var body = CreateBody(fx, clientId, OwnBilling(fx, [fx.Retefuente, fx.ReteIcaBogota]));

        var preview = await ReadAsync(await PostCreatePreviewAsync(fx, body));
        var created = await ReadAsync(await PostCreateAsync(fx, body), HttpStatusCode.Created);

        foreach (var quotation in new[] { preview, created })
        {
            var reteIca = Assert.Single(quotation.Withholdings, row => row.Type == "ReteICA");
            Assert.Equal((fx.ReteIcaBogota, (Guid?)fx.Bogota.CityId, 0.966m), (reteIca.TaxRateId, reteIca.CityId, reteIca.Percentage));
            Assert.Contains(quotation.Withholdings, row => row.TaxRateId == fx.Retefuente);
            Assert.Equal("Resolved", Assert.Single(quotation.MunicipalWithholdings).Status);
        }

        var history = await HistoryAsync(fx, created.Id);
        Assert.Contains(history, item => item.EventType == "Created");
        Assert.DoesNotContain(history, item => item.EventType == "WithholdingRateEdited");
    }

    [Fact]
    public async Task CreateWithTheCompanysReteIcaAndAPercentageAppliesBoth()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var body = CreateBody(fx, clientId, OwnBilling(fx, [fx.ReteIcaBogota])) with
        {
            WithholdingPercentages = [new("ReteICA", 0.8m)],
        };

        var preview = await ReadAsync(await PostCreatePreviewAsync(fx, body));
        var created = await ReadAsync(await PostCreateAsync(fx, body), HttpStatusCode.Created);

        foreach (var quotation in new[] { preview, created })
        {
            var row = Assert.Single(quotation.Withholdings);
            Assert.Equal((0.8m, 0.966m, true, 800m), (row.Percentage, row.CatalogPercentage, row.PercentageOverridden, row.Amount));
        }

        Assert.Empty(await RateEditedAsync(fx, created.Id));
    }

    [Fact]
    public async Task CreateWithAnExplicitOperationCityValidatesAgainstIt()
    {
        var fx = await SetUpAsync();
        var medellinRate = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Medellín", "ReteICA", 1m, cityId: fx.Medellin.CityId);
        await AssignCompanyWithholdingRateAsync(fx.Factory, fx.TenantId, fx.BogotaAccount.CompanyId, medellinRate);
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var good = CreateBody(fx, clientId, OwnBilling(fx, [medellinRate])) with { OperationCityId = Json(fx.Medellin.CityId) };
        var wrong = CreateBody(fx, clientId, OwnBilling(fx, [fx.ReteIcaBogota])) with { OperationCityId = Json(fx.Medellin.CityId) };

        var preview = await ReadAsync(await PostCreatePreviewAsync(fx, good));
        var created = await ReadAsync(await PostCreateAsync(fx, good), HttpStatusCode.Created);

        foreach (var quotation in new[] { preview, created })
        {
            Assert.Equal(medellinRate, Assert.Single(quotation.Withholdings).TaxRateId);
            Assert.Equal(fx.Medellin.CityId, quotation.OperationCity?.Id);
        }

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", await CodeAsync(await PostCreatePreviewAsync(fx, wrong)));
        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", await CodeAsync(await PostCreateAsync(fx, wrong)));
    }

    // Review Focus 8: en la creación un id de otro municipio nunca se ignora (no hay cambio de contexto que lo justifique).
    [Fact]
    public async Task CreateWithAReteIcaOfAnotherCityIs422()
    {
        var fx = await SetUpAsync();
        var medellinRate = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Medellín", "ReteICA", 1m, cityId: fx.Medellin.CityId);
        await AssignCompanyWithholdingRateAsync(fx.Factory, fx.TenantId, fx.BogotaAccount.CompanyId, medellinRate);
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var body = CreateBody(fx, clientId, OwnBilling(fx, [medellinRate]));
        var first = await CreateAsync(fx, clientId);

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", await CodeAsync(await PostCreatePreviewAsync(fx, body)));
        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", await CodeAsync(await PostCreateAsync(fx, body)));

        // El 422 no gastó un número del consecutivo.
        var next = await CreateAsync(fx, clientId);
        Assert.Equal(NumberOf(first.QuotationNumber) + 1, NumberOf(next.QuotationNumber));
    }

    [Fact]
    public async Task CreateWithAPercentageAndNoRowIs422()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var body = CreateBody(fx, clientId, OwnBilling(fx, [fx.Retefuente])) with
        {
            WithholdingPercentages = [new("ReteICA", 0.8m)],
        };

        Assert.Equal("quotation.billing.withholding_percentage_not_editable", await CodeAsync(await PostCreatePreviewAsync(fx, body)));
        Assert.Equal("quotation.billing.withholding_percentage_not_editable", await CodeAsync(await PostCreateAsync(fx, body)));
    }

    [Fact]
    public async Task CreateWithClearedAndSelectedIsConflict()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var body = CreateBody(fx, clientId, OwnBilling(fx, [fx.ReteIcaBogota])) with { ClearedWithholdingTypes = ["ReteICA"] };

        Assert.Equal("quotation.billing.withholding_selection_conflict", await CodeAsync(await PostCreatePreviewAsync(fx, body)));
        Assert.Equal("quotation.billing.withholding_selection_conflict", await CodeAsync(await PostCreateAsync(fx, body)));
    }

    [Fact]
    public async Task CreateWithClearedReteIcaHasNoRow()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var body = CreateBody(fx, clientId, OwnBilling(fx, [fx.Retefuente])) with { ClearedWithholdingTypes = ["ReteICA"] };

        var preview = await ReadAsync(await PostCreatePreviewAsync(fx, body));
        var created = await ReadAsync(await PostCreateAsync(fx, body), HttpStatusCode.Created);

        foreach (var quotation in new[] { preview, created })
        {
            Assert.Equal(fx.Retefuente, Assert.Single(quotation.Withholdings).TaxRateId);
            Assert.DoesNotContain(quotation.BillingWithholdingTaxRates, rate => rate.Type == "ReteICA");
        }
    }

    // Review Focus 2: un documento anterior (sin municipio guardado) que reenvía la misma empresa completa su municipio sin
    // re-resolver ni escribir historial; fijar después el municipio resuelve la ReteICA.
    [Fact]
    public async Task ResendingTheSameCompanyFillsTheCompanyCityWithoutReResolving()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var created = await CreateAsync(fx, clientId);
        await ExecuteAsync(
            fx,
            $"UPDATE quotations.quotations SET operation_city_id = NULL, billing_company_city_id = NULL WHERE id = '{created.Id}'");

        var legacy = await GetAsync(fx, created.Id);
        Assert.Null(legacy.BillingCompanyCity);
        Assert.Null(legacy.OperationCity);
        Assert.Equal("NoOperationCity", Assert.Single(legacy.MunicipalWithholdings).Status);
        var historyBefore = (await HistoryAsync(fx, created.Id)).Count;

        var resent = await ReadAsync(await PutAsync(fx, legacy, Body(legacy)));

        Assert.Equal(fx.Bogota.CityId, resent.BillingCompanyCity?.Id);
        Assert.Null(resent.OperationCity);
        Assert.Equal(historyBefore, (await HistoryAsync(fx, created.Id)).Count);

        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, clientId, fx.Bogota.CityId);
        var fixedCity = await ReadAsync(await PutAsync(fx, resent, Body(resent) with { OperationCityId = Json(fx.Bogota.CityId) }));

        Assert.Equal(fx.ReteIcaBogota, Assert.Single(fixedCity.Withholdings).TaxRateId);
        Assert.Equal("Resolved", Assert.Single(fixedCity.MunicipalWithholdings).Status);
    }

    // A14: el status se deriva al leer con los agentes de hoy; la foto y los totales no se mueven.
    [Fact]
    public async Task StatusFollowsTodaysAgentsWithoutMovingThePhoto()
    {
        var fx = await SetUpAsync();
        var agent = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateAsync(fx, agent);
        var notAgent = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var withoutRow = await CreateAsync(fx, notAgent);

        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, agent, fx.Bogota.CityId, remove: true);
        var fetched = await GetAsync(fx, created.Id);

        var entry = Assert.Single(fetched.MunicipalWithholdings);
        Assert.Equal(("Resolved", (bool?)false), (entry.Status, entry.CustomerIsAgent));
        Assert.Equal(created.RetentionAmount, fetched.RetentionAmount);
        Assert.Equal(
            ("CustomerNotAgent", (bool?)false),
            (Assert.Single(withoutRow.MunicipalWithholdings).Status, withoutRow.MunicipalWithholdings[0].CustomerIsAgent));

        // El cliente pasó a ser agente después de resolverse: sin fila, con municipio y tasa, NotSelected (§9.4, rama 4).
        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, notAgent, fx.Bogota.CityId);
        var nowAgent = Assert.Single((await GetAsync(fx, withoutRow.Id)).MunicipalWithholdings);
        Assert.Equal(("NotSelected", (bool?)true), (nowAgent.Status, nowAgent.CustomerIsAgent));
        Assert.Empty((await GetAsync(fx, withoutRow.Id)).Withholdings);
    }

    // Spec §9.4: con facturación propia y sin municipio guardado (documento anterior) la rama es NoOperationCity y el
    // tercero no tiene lista de agentes.
    [Fact]
    public async Task OwnBillingWithoutOperationCityIsNoOperationCity()
    {
        var fx = await SetUpAsync();
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var created = await ReadAsync(
            await PostCreateAsync(fx, CreateBody(fx, clientId, OwnBilling(fx, []))), HttpStatusCode.Created);
        await ExecuteAsync(fx, $"UPDATE quotations.quotations SET operation_city_id = NULL WHERE id = '{created.Id}'");

        var entry = Assert.Single((await GetAsync(fx, created.Id)).MunicipalWithholdings);

        Assert.Equal(("NoOperationCity", (bool?)null), (entry.Status, entry.CustomerIsAgent));
    }

    [Fact]
    public async Task ChangingTheClientReResolvesWithItsAgents()
    {
        var fx = await SetUpAsync();
        var agent = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var notAgent = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var created = await CreateAsync(fx, agent);

        var toNotAgent = await ReadAsync(await fx.Client.PutAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/{created.Id}/client", new { clientId = notAgent }, TestContext.Current.CancellationToken));
        var backToAgent = await ReadAsync(await fx.Client.PutAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/{created.Id}/client", new { clientId = agent }, TestContext.Current.CancellationToken));

        Assert.Empty(toNotAgent.Withholdings);
        Assert.Equal("CustomerNotAgent", Assert.Single(toNotAgent.MunicipalWithholdings).Status);
        Assert.Equal(fx.ReteIcaBogota, Assert.Single(backToAgent.Withholdings).TaxRateId);
    }

    // ---- Fixture ----

    private sealed record Fixture(
        QepApiFactory Factory,
        HttpClient Client,
        Guid TenantId,
        Guid ProductId,
        QuotationBillingAccountRequest BogotaAccount,
        CityInDepartment Bogota,
        CityInDepartment Medellin,
        Guid ReteIcaBogota,
        Guid Retefuente);

    private sealed record A11Start(QuotationResponse Edited, QuotationBillingAccountRequest MedellinAccount, Guid MedellinRate);

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
        var retefuente = await InsertWithholdingRateAsync(factory, tenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        return new Fixture(
            factory, client, tenantId, productId,
            new QuotationBillingAccountRequest(company.CompanyId, company.BankName, company.AccountNumber, company.Currency),
            bogota, medellin, reteIca, retefuente);
    }

    // Una cotización con facturación propia y la ReteICA Bogotá editada a 0,8, y una segunda empresa CO en Medellín con
    // «ReteICA Medellín» 1 % asignada; el cliente es agente en los dos municipios.
    private static async Task<A11Start> A11StartAsync(Fixture fx)
    {
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, clientId, fx.Medellin.CityId);
        var medellin = await CreateCompanyWithBankAccountAsync(fx.Client, fx.TenantId, cityId: fx.Medellin.CityId);
        var medellinRate = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Medellín", "ReteICA", 1m, cityId: fx.Medellin.CityId);
        await AssignCompanyWithholdingRateAsync(fx.Factory, fx.TenantId, medellin.CompanyId, medellinRate);
        var created = await CreateAsync(fx, clientId);
        var edited = await ReadAsync(await PutAsync(
            fx, created, Body(created, OwnBilling(fx, [fx.ReteIcaBogota])) with { WithholdingPercentages = [new("ReteICA", 0.8m)] }));
        Assert.Equal(0.8m, Assert.Single(edited.Withholdings).Percentage);
        return new A11Start(
            edited,
            new QuotationBillingAccountRequest(medellin.CompanyId, medellin.BankName, medellin.AccountNumber, medellin.Currency),
            medellinRate);
    }

    // La empresa cambia su ReteICA de Bogotá por otra (A18): la vieja sigue activa en el catálogo, pero ya no es la que ofrece.
    private static async Task<Guid> ReplaceBogotaRateAsync(Fixture fx)
    {
        await ExecuteAsync(
            fx,
            $"DELETE FROM companies.company_withholding_tax_rates WHERE company_id = '{fx.BogotaAccount.CompanyId}' AND tax_rate_id = '{fx.ReteIcaBogota}'");
        var replacement = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Bogotá 2027", "ReteICA", 1.1m, cityId: fx.Bogota.CityId);
        await AssignCompanyWithholdingRateAsync(fx.Factory, fx.TenantId, fx.BogotaAccount.CompanyId, replacement);
        return replacement;
    }

    private static async Task<Guid> AgentCustomerAsync(Fixture fx, Guid cityId)
    {
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, clientId, cityId);
        return clientId;
    }

    // Completa: una parte incompleta no deja guardar.
    private static QuotationPartiesRequest OwnBilling(Fixture fx, IReadOnlyList<Guid> withholdingTaxRateIds) =>
        new(
            new QuotationPartyRequest(
                "Sede administrativa", "1020304050", "3001234567", "sede@verde.co", "Calle 80 # 10-20",
                fx.Bogota.DepartmentId, fx.Bogota.CityId, PersonType: "Natural", IdentificationType: "CC", Country: "CO"),
            null,
            BillingVatSurplus: false,
            BillingWithholdingTaxRateIds: withholdingTaxRateIds);

    private static CreateQuotationRequest CreateBody(Fixture fx, Guid clientId, QuotationPartiesRequest? parties = null) =>
        new(
            clientId, TodayInBogota().AddDays(30), null, null, parties, fx.BogotaAccount,
            Items: [new QuotationEditItemRequest(fx.ProductId, 1m)]);

    private static async Task<QuotationResponse> CreateAsync(Fixture fx, Guid clientId) =>
        await ReadAsync(await PostCreateAsync(fx, CreateBody(fx, clientId)), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> PostCreateAsync(Fixture fx, CreateQuotationRequest body) =>
        fx.Client.PostAsJsonAsync(QuotationsUrl(fx.TenantId), body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostCreatePreviewAsync(Fixture fx, CreateQuotationRequest body) =>
        fx.Client.PostAsJsonAsync($"{QuotationsUrl(fx.TenantId)}/preview", body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostPreviewAsync(Fixture fx, QuotationResponse quotation, SaveQuotationRequest body) =>
        fx.Client.PostAsJsonAsync($"{QuotationsUrl(fx.TenantId)}/{quotation.Id}/preview", body, TestContext.Current.CancellationToken);

    // El cuerpo entero, como lo manda el editor: sin partes propias vuelve a los datos del cliente.
    private static SaveQuotationRequest Body(
        QuotationResponse quotation, QuotationPartiesRequest? parties = null, QuotationBillingAccountRequest? account = null) =>
        new(
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Notes,
            parties,
            account ?? (quotation.BillingAccount is { } billing
                ? new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency)
                : null),
            [.. quotation.Items.Select(item => new QuotationEditItemRequest(item.VariantId, item.Quantity))]);

    private static async Task<HttpResponseMessage> PutAsync(Fixture fx, QuotationResponse quotation, SaveQuotationRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{QuotationsUrl(fx.TenantId)}/{quotation.Id}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        return await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static JsonElement Json(Guid cityId) => JsonSerializer.SerializeToElement(cityId);

    private static async Task<QuotationResponse> GetAsync(Fixture fx, Guid quotationId) =>
        await ReadAsync(await fx.Client.GetAsync($"{QuotationsUrl(fx.TenantId)}/{quotationId}", TestContext.Current.CancellationToken));

    private static async Task<IReadOnlyList<QuotationHistoryEntryDto>> HistoryAsync(Fixture fx, Guid quotationId)
    {
        var history = await fx.Client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(fx.TenantId)}/{quotationId}/history", TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        return [.. history.Items];
    }

    // Las entradas de tarifa, de la más vieja a la más nueva.
    private static async Task<IReadOnlyList<string>> RateEditedAsync(Fixture fx, Guid quotationId) =>
        [.. (await HistoryAsync(fx, quotationId))
            .Where(item => item.EventType == "WithholdingRateEdited")
            .OrderBy(item => item.EventAt)
            .Select(item => item.Details!)];

    private static async Task ExecuteAsync(Fixture fx, string sql) =>
        Assert.Equal(1, await ExecuteSqlAsync(fx.Factory, sql));

    private static int NumberOf(string quotationNumber) =>
        int.Parse(new string([.. quotationNumber.Reverse().TakeWhile(char.IsDigit).Reverse()]), System.Globalization.CultureInfo.InvariantCulture);

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

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
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        return body.RootElement.GetProperty("code").GetString();
    }
}
