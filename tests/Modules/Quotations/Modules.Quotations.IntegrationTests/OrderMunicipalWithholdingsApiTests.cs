using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec ReteICA por empresa §7.5–§7.8, §9.4 del lado del pedido: el pedido hereda municipio y porcentaje al convertir, nace
/// en el municipio de la empresa con la regla del cliente, se edita (municipio, selector municipal con aplicación a mano,
/// porcentaje, «Sin retención») mientras es editable, y explica al leer (<c>municipalWithholdings</c> en la raíz de
/// <c>OrderDetailResponse</c>, plan P2). Mismo fixture que <see cref="QuotationMunicipalWithholdingsApiTests"/>.
/// </summary>
public sealed class OrderMunicipalWithholdingsApiTests(SharedQuotationsHost sharedHost)
    : IClassFixture<SharedQuotationsHost>
{
    private const string NotAgentSuffix = "el cliente no está registrado como agente retenedor de ReteICA en";

    [Fact]
    public async Task ConvertedOrderInheritsCityAndEditedPercentage()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);

        var order = await ConvertEditedQuotationAsync(fx, clientId, 0.8m);

        var row = Assert.Single(order.Order.Withholdings);
        Assert.Equal(
            (fx.ReteIcaBogota, 0.8m, 0.966m, true, 800m),
            (row.TaxRateId, row.Percentage, row.CatalogPercentage, row.PercentageOverridden, row.Amount));
        Assert.Equal(fx.Bogota.CityId, order.OperationCity?.Id);
        Assert.Equal(fx.Bogota.CityId, order.BillingCompanyCity?.Id);
        var entry = Assert.Single(order.MunicipalWithholdings);
        Assert.Equal(("Resolved", (bool?)true, fx.ReteIcaBogota), (entry.Status, entry.CustomerIsAgent, entry.CompanyRate?.TaxRateId));
    }

    [Fact]
    public async Task ManualOrderStartsWithTheCompanysCity()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);

        var order = await CreateManualAsync(fx, clientId);

        Assert.Equal(fx.ReteIcaBogota, Assert.Single(order.Order.Withholdings).TaxRateId);
        Assert.Equal(fx.Bogota.CityId, order.OperationCity?.Id);
        Assert.Equal(("Resolved", (bool?)true), (order.MunicipalWithholdings[0].Status, order.MunicipalWithholdings[0].CustomerIsAgent));
    }

    // R11: en la edición del pedido el asesor aplica la tasa de la empresa aunque el cliente no sea agente ahí; cambiar después
    // el municipio la quita con la regla del cliente y lo anota.
    [Fact]
    public async Task HandAppliedRateIsResolvedWithCustomerIsAgentFalse()
    {
        var fx = await SetUpAsync();
        var medellinRate = await AssignMedellinRateAsync(fx, fx.BogotaAccount.CompanyId);
        var clientId = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var created = await CreateManualAsync(fx, clientId);
        Assert.Equal("CustomerNotAgent", Assert.Single(created.MunicipalWithholdings).Status);

        var applied = await ReadDetailAsync(await PutAsync(fx, created, Edits(fx, [fx.ReteIcaBogota])));

        Assert.Equal(fx.ReteIcaBogota, Assert.Single(applied.Order.Withholdings).TaxRateId);
        Assert.Equal(("Resolved", (bool?)false), (applied.MunicipalWithholdings[0].Status, applied.MunicipalWithholdings[0].CustomerIsAgent));
        Assert.Contains(
            applied.History,
            item => item.EventType == "PartiesChanged" && item.Details!.Contains(NotAgentSuffix, StringComparison.Ordinal));

        var moved = await ReadDetailAsync(await PutAsync(fx, applied, Edits(fx) with { OperationCityId = Json(fx.Medellin.CityId) }));

        Assert.Empty(moved.Order.Withholdings);
        Assert.Equal(fx.Medellin.CityId, moved.OperationCity?.Id);
        Assert.Equal(medellinRate, moved.MunicipalWithholdings[0].CompanyRate?.TaxRateId);
        Assert.Contains(
            "Se quitó la ReteICA aplicada a mano al cambiar la empresa o el municipio: el cliente no es agente retenedor ahí.",
            RateEdited(moved));
        Assert.Contains(
            moved.History,
            item => item.EventType == "PartiesChanged"
                && item.Details!.StartsWith("Cambió el municipio de la operación de ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusBranchesOnOrders()
    {
        var fx = await SetUpAsync();
        var notAgent = await CreateActiveCustomerAsync(fx.Client, fx.TenantId);
        var agent = await AgentCustomerAsync(fx, fx.Bogota.CityId);

        var withoutRow = await CreateManualAsync(fx, notAgent);
        var agentOrder = await CreateManualAsync(fx, agent);
        var emptied = await ReadDetailAsync(await PutAsync(fx, agentOrder, Edits(fx, [])));

        Assert.Equal("CustomerNotAgent", Assert.Single(withoutRow.MunicipalWithholdings).Status);
        Assert.Empty(emptied.Order.Withholdings);
        Assert.Equal(("NotSelected", (bool?)true), (emptied.MunicipalWithholdings[0].Status, emptied.MunicipalWithholdings[0].CustomerIsAgent));

        // Un pedido anterior a este trabajo, sin municipio guardado (A8): NoOperationCity y sin consultar agentes.
        Assert.Equal(
            1,
            await ExecuteSqlAsync(
                fx.Factory, $"UPDATE quotations.orders SET operation_city_id = NULL WHERE id = '{withoutRow.Order.Id}'"));
        var legacy = Assert.Single((await GetDetailAsync(fx, withoutRow.Order.Id)).MunicipalWithholdings);
        Assert.Equal(("NoOperationCity", (bool?)null), (legacy.Status, legacy.CustomerIsAgent));
    }

    [Fact]
    public async Task PreparedOrderRejectsCityAndPercentage()
    {
        var fx = await SetUpAsync(ApproverPermissions);
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateManualAsync(fx, clientId);
        var prepare = await fx.Client.PostAsync(
            $"{OrdersUrl(fx.TenantId)}/{created.Order.Id}/prepare", null, TestContext.Current.CancellationToken);
        prepare.EnsureSuccessStatusCode();
        var prepared = await GetDetailAsync(fx, created.Order.Id);

        var city = await PutAsync(fx, prepared, Edits(fx) with { OperationCityId = Json(fx.Medellin.CityId) });
        var percentage = await PutAsync(fx, prepared, Edits(fx) with { WithholdingPercentages = [new("ReteICA", 0.8m)] });

        Assert.Equal("order.order.not_editable", await CodeAsync(city));
        Assert.Equal("order.order.not_editable", await CodeAsync(percentage));
    }

    [Fact]
    public async Task EditingThePercentageLogsAndAudits()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateManualAsync(fx, clientId);

        var saved = await ReadDetailAsync(await PutAsync(fx, created, Edits(fx) with { WithholdingPercentages = [new("ReteICA", 0.8m)] }));

        Assert.Equal((0.8m, 800m), (saved.Order.Withholdings[0].Percentage, saved.Order.RetentionAmount));
        Assert.Equal(["Cambió la tarifa de ReteICA Bogotá de 0,966 % a 0,8 %."], RateEdited(saved));
        Assert.Contains(
            "quotation.order.withholding_rate_edited",
            (await OutboxMessagesAsync(fx.Factory, "platform.audit.recorded.v1")).Select(ActionOf));
        Assert.Equal(
            "order.billing.withholding_percentage_invalid",
            await CodeAsync(await PutAsync(fx, saved, Edits(fx) with { WithholdingPercentages = [new("ReteICA", 0.8125m)] })));
        Assert.Equal(
            "order.billing.withholding_percentage_not_editable",
            await CodeAsync(await PutAsync(fx, saved, Edits(fx) with { WithholdingPercentages = [new("Retefuente", 1m)] })));
        Assert.Equal(
            "order.billing.operation_city_invalid",
            await CodeAsync(await PutAsync(fx, saved, Edits(fx) with { OperationCityId = Json(Guid.NewGuid()) })));
    }

    // A11 en el pedido (owner 2026-11-13): el cambio de municipio acepta la tasa de la empresa para el municipio nuevo y su
    // porcentaje; la aplicación a mano (R11) no cambia: se acepta aunque el cliente no sea agente ahí.
    [Fact]
    public async Task CityChangeAcceptsTheNewCompanysRate()
    {
        var fx = await SetUpAsync();
        var medellinRate = await AssignMedellinRateAsync(fx, fx.BogotaAccount.CompanyId);
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateManualAsync(fx, clientId);
        var body = Edits(fx, [medellinRate]) with
        {
            OperationCityId = Json(fx.Medellin.CityId),
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        };

        var preview = await ReadDetailAsync(await PostPreviewAsync(fx, created, body));
        var saved = await ReadDetailAsync(await PutAsync(fx, created, body));

        foreach (var order in new[] { preview, saved })
        {
            var row = Assert.Single(order.Order.Withholdings);
            Assert.Equal((medellinRate, 0.7m), (row.TaxRateId, row.Percentage));
            Assert.Equal(("Resolved", (bool?)false), (order.MunicipalWithholdings[0].Status, order.MunicipalWithholdings[0].CustomerIsAgent));
        }

        Assert.Contains(
            saved.History,
            item => item.EventType == "PartiesChanged" && item.Details!.Contains(NotAgentSuffix, StringComparison.Ordinal));
        Assert.Equal("Cambió la tarifa de ReteICA Medellín de 1 % a 0,7 %.", RateEdited(saved)[^1]);
    }

    [Fact]
    public async Task CityChangeIgnoresTheOldCompanysRate()
    {
        var fx = await SetUpAsync();
        var medellinRate = await AssignMedellinRateAsync(fx, fx.BogotaAccount.CompanyId);
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, clientId, fx.Medellin.CityId);
        var created = await CreateManualAsync(fx, clientId);
        var body = Edits(fx, [fx.ReteIcaBogota]) with
        {
            OperationCityId = Json(fx.Medellin.CityId),
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        };

        var preview = await ReadDetailAsync(await PostPreviewAsync(fx, created, body));
        var saved = await ReadDetailAsync(await PutAsync(fx, created, body));

        foreach (var order in new[] { preview, saved })
        {
            var row = Assert.Single(order.Order.Withholdings);
            Assert.Equal((medellinRate, 0.7m), (row.TaxRateId, row.Percentage));
        }

        // Sin tasa de la empresa en el municipio no queda fila y el porcentaje se ignora sin 422.
        var other = await CreateCompanyWithBankAccountAsync(fx.Client, fx.TenantId, cityId: fx.Medellin.CityId);
        var bare = Edits(fx, [medellinRate]) with
        {
            BillingAccount = new QuotationBillingAccountRequest(other.CompanyId, other.BankName, other.AccountNumber, other.Currency),
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        };
        Assert.Empty((await ReadDetailAsync(await PostPreviewAsync(fx, saved, bare))).Order.Withholdings);
        var noRow = await ReadDetailAsync(await PutAsync(fx, saved, bare));
        Assert.Empty(noRow.Order.Withholdings);
        Assert.Equal("NoCompanyRate", noRow.MunicipalWithholdings[0].Status);
    }

    [Fact]
    public async Task CityChangeWithClearedReteIcaSavesNoRow()
    {
        var fx = await SetUpAsync();
        await AssignMedellinRateAsync(fx, fx.BogotaAccount.CompanyId);
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        await SetWithholdingAgentAsync(fx.Factory, fx.TenantId, clientId, fx.Medellin.CityId);
        var edited = await EditedManualAsync(fx, clientId);
        var body = Edits(fx, [fx.Retefuente]) with
        {
            OperationCityId = Json(fx.Medellin.CityId),
            ClearedWithholdingTypes = ["ReteICA"],
        };

        var preview = await ReadDetailAsync(await PostPreviewAsync(fx, edited, body));
        Assert.Single(RateEdited(await GetDetailAsync(fx, edited.Order.Id)));
        var saved = await ReadDetailAsync(await PutAsync(fx, edited, body));

        foreach (var order in new[] { preview, saved })
        {
            Assert.Equal(fx.Retefuente, Assert.Single(order.Order.Withholdings).TaxRateId);
        }

        Assert.Contains(
            saved.History,
            item => item.EventType == "PartiesChanged" && item.Details!.StartsWith("Cambió las retenciones de ", StringComparison.Ordinal));
        Assert.Single(RateEdited(saved), text => text.Contains("se descartó la tarifa editada (0,8 %)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearedAndSelectedIsConflict()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateManualAsync(fx, clientId);

        var withId = await PutAsync(fx, created, Edits(fx, [fx.ReteIcaBogota]) with { ClearedWithholdingTypes = ["ReteICA"] });
        var withPercentage = await PutAsync(fx, created, Edits(fx) with
        {
            ClearedWithholdingTypes = ["ReteICA"],
            WithholdingPercentages = [new("ReteICA", 0.7m)],
        });
        var retefuente = await PutAsync(fx, created, Edits(fx) with { ClearedWithholdingTypes = ["Retefuente"] });

        Assert.Equal("order.billing.withholding_selection_conflict", await CodeAsync(withId));
        Assert.Equal("order.billing.withholding_selection_conflict", await CodeAsync(withPercentage));
        Assert.Equal("order.billing.withholding_type_invalid", await CodeAsync(retefuente));
    }

    [Fact]
    public async Task ClearedWithoutContextChangeRemovesTheRow()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var edited = await EditedManualAsync(fx, clientId);

        var saved = await ReadDetailAsync(await PutAsync(fx, edited, Edits(fx) with { ClearedWithholdingTypes = ["ReteICA"] }));

        Assert.Empty(saved.Order.Withholdings);
        Assert.Equal(0m, saved.Order.RetentionAmount);
        Assert.Contains(
            saved.History,
            item => item.EventType == "PartiesChanged" && item.Details == "Cambió las retenciones de ReteICA Bogotá a ninguna.");
        Assert.Equal(
            "Se volvió a calcular la ReteICA por cambio de municipio o de empresa; se descartó la tarifa editada (0,8 %).",
            RateEdited(saved)[^1]);
    }

    // Review Focus 1, gemelo del pedido.
    [Fact]
    public async Task ResendingTheSameMunicipalSelectorKeepsTheEditedPercentage()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateManualAsync(fx, clientId);
        var edited = await ReadDetailAsync(await PutAsync(
            fx, created, Edits(fx, [fx.ReteIcaBogota]) with { WithholdingPercentages = [new("ReteICA", 0.8m)] }));

        var resent = await ReadDetailAsync(await PutAsync(fx, edited, Edits(fx, [fx.ReteIcaBogota])));

        var row = Assert.Single(resent.Order.Withholdings);
        Assert.Equal((0.8m, true, 800m), (row.Percentage, row.PercentageOverridden, row.Amount));
        Assert.Single(RateEdited(resent));
    }

    // A18 (owner 2026-11-14), gemelo del pedido: la empresa reemplaza su tasa de Bogotá y el pedido se guarda con la lista
    // completa, la guardada incluida, en otro orden que el guardado para que el selector se vuelva a resolver.
    [Fact]
    public async Task ReplacedCompanyRateKeepsTheStoredIdOnANotesOnlySave()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateManualAsync(fx, clientId);
        var stored = await ReadDetailAsync(await PutAsync(fx, created, Edits(fx, [fx.ReteIcaBogota, fx.Retefuente])));
        var replacement = await ReplaceBogotaRateAsync(fx);

        var saved = await ReadDetailAsync(await PutAsync(fx, stored, Edits(fx, [fx.Retefuente, fx.ReteIcaBogota])));

        var reteIca = Assert.Single(saved.Order.Withholdings, row => row.Type == "ReteICA");
        Assert.Equal((fx.ReteIcaBogota, 0.966m, false), (reteIca.TaxRateId, reteIca.Percentage, reteIca.PercentageOverridden));
        Assert.Equal(replacement, Assert.Single(saved.MunicipalWithholdings).CompanyRate?.TaxRateId);
        Assert.Empty(RateEdited(saved));
    }

    // A18: cambiar sólo un selector nacional no relee el catálogo para la fila municipal guardada; el 0,8 % sobrevive.
    [Fact]
    public async Task ChangingANationalSelectorKeepsTheEditedMunicipalPercentage()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var edited = await EditedManualAsync(fx, clientId);

        var body = Edits(fx, [fx.ReteIcaBogota, fx.Retefuente]);
        var preview = await ReadDetailAsync(await PostPreviewAsync(fx, edited, body));
        var saved = await ReadDetailAsync(await PutAsync(fx, edited, body));

        foreach (var order in new[] { preview, saved })
        {
            var reteIca = Assert.Single(order.Order.Withholdings, row => row.Type == "ReteICA");
            Assert.Equal((0.8m, 0.966m, true), (reteIca.Percentage, reteIca.CatalogPercentage, reteIca.PercentageOverridden));
            Assert.Contains(order.Order.Withholdings, row => row.TaxRateId == fx.Retefuente);
        }

        Assert.Single(RateEdited(saved));
    }

    // Control de A18: un id municipal que no es ni el guardado ni el de la empresa sigue siendo 422.
    [Fact]
    public async Task AMunicipalIdNeitherStoredNorOfferedIs422()
    {
        var fx = await SetUpAsync();
        var clientId = await AgentCustomerAsync(fx, fx.Bogota.CityId);
        var created = await CreateManualAsync(fx, clientId);
        var unassigned = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Bogotá sin asignar", "ReteICA", 1.2m, cityId: fx.Bogota.CityId);

        var response = await PutAsync(fx, created, Edits(fx, [unassigned, fx.Retefuente]));

        Assert.Equal("order.billing.withholding_tax_rate_invalid", await CodeAsync(response));
    }

    // ---- Fixture ----

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

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

    private async Task<Fixture> SetUpAsync(string[]? permissions = null)
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, permissions ?? ManagerPermissions);
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

    private static async Task<Guid> AssignMedellinRateAsync(Fixture fx, Guid companyId)
    {
        var rate = await InsertWithholdingRateAsync(
            fx.Factory, fx.TenantId, "ReteICA Medellín", "ReteICA", 1m, cityId: fx.Medellin.CityId);
        await AssignCompanyWithholdingRateAsync(fx.Factory, fx.TenantId, companyId, rate);
        return rate;
    }

    // La empresa cambia su ReteICA de Bogotá por otra (A18): la vieja sigue activa en el catálogo, pero ya no es la que ofrece.
    private static async Task<Guid> ReplaceBogotaRateAsync(Fixture fx)
    {
        Assert.Equal(1, await ExecuteSqlAsync(
            fx.Factory,
            $"DELETE FROM companies.company_withholding_tax_rates WHERE company_id = '{fx.BogotaAccount.CompanyId}' AND tax_rate_id = '{fx.ReteIcaBogota}'"));
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

    private static async Task<OrderDetailResponse> CreateManualAsync(Fixture fx, Guid clientId) =>
        await ReadDetailAsync(await fx.Client.PostAsJsonAsync(
            OrdersUrl(fx.TenantId),
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null, fx.BogotaAccount,
                [new OrderEditItemRequest(fx.ProductId, 1m)], []),
            TestContext.Current.CancellationToken), HttpStatusCode.Created);

    // Un pedido manual con la ReteICA Bogotá editada a 0,8.
    private static async Task<OrderDetailResponse> EditedManualAsync(Fixture fx, Guid clientId)
    {
        var created = await CreateManualAsync(fx, clientId);
        var edited = await ReadDetailAsync(await PutAsync(fx, created, Edits(fx) with { WithholdingPercentages = [new("ReteICA", 0.8m)] }));
        Assert.Equal(0.8m, Assert.Single(edited.Order.Withholdings).Percentage);
        return edited;
    }

    // Cotización con la ReteICA editada, enviada y convertida.
    private static async Task<OrderDetailResponse> ConvertEditedQuotationAsync(Fixture fx, Guid clientId, decimal percentage)
    {
        var created = await ReadQuotationAsync(await fx.Client.PostAsJsonAsync(
            QuotationsUrl(fx.TenantId),
            new CreateQuotationRequest(
                clientId, TodayInBogota().AddDays(30), "Transferencia", null, null, fx.BogotaAccount,
                Items: [new QuotationEditItemRequest(fx.ProductId, 1m)],
                WithholdingPercentages: [new("ReteICA", percentage)]),
            TestContext.Current.CancellationToken), HttpStatusCode.Created);
        Assert.Equal(percentage, Assert.Single(created.Withholdings).Percentage);
        var send = await fx.Client.PostAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/{created.Id}/send",
            new SendQuotationRequest(await CreateAvailablePdfFileAsync(fx.Client, fx.Factory, fx.TenantId)),
            TestContext.Current.CancellationToken);
        send.EnsureSuccessStatusCode();
        var conversion = await fx.Client.PostAsJsonAsync(
            $"{QuotationsUrl(fx.TenantId)}/{created.Id}/order",
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        Assert.True(
            conversion.IsSuccessStatusCode,
            await conversion.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var converted = await conversion.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(converted);
        return await GetDetailAsync(fx, converted.Id);
    }

    private static SaveOrderEditsRequest Edits(Fixture fx, IReadOnlyList<Guid>? withholdingTaxRateIds = null) =>
        new([new OrderEditItemRequest(fx.ProductId, 1m)], null, null, WithholdingTaxRateIds: withholdingTaxRateIds);

    private static JsonElement Json(Guid cityId) => JsonSerializer.SerializeToElement(cityId);

    private static async Task<HttpResponseMessage> PutAsync(Fixture fx, OrderDetailResponse order, SaveOrderEditsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{OrdersUrl(fx.TenantId)}/{order.Order.Id}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{order.Order.Version}\"");
        return await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PostPreviewAsync(Fixture fx, OrderDetailResponse order, SaveOrderEditsRequest body) =>
        fx.Client.PostAsJsonAsync($"{OrdersUrl(fx.TenantId)}/{order.Order.Id}/preview", body, TestContext.Current.CancellationToken);

    private static async Task<OrderDetailResponse> GetDetailAsync(Fixture fx, Guid orderId) =>
        await ReadDetailAsync(await fx.Client.GetAsync($"{OrdersUrl(fx.TenantId)}/{orderId}", TestContext.Current.CancellationToken));

    // Las entradas de tarifa, de la más vieja a la más nueva.
    private static List<string> RateEdited(OrderDetailResponse order) =>
        [.. order.History
            .Where(item => item.EventType == "WithholdingRateEdited")
            .OrderBy(item => item.EventAt)
            .Select(item => item.Details!)];

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private static async Task<OrderDetailResponse> ReadDetailAsync(
        HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        var detail = JsonSerializer.Deserialize<OrderDetailResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response, HttpStatusCode expected)
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
