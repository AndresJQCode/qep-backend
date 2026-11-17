using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Purchases.Application;
using static Modules.Purchases.IntegrationTests.PurchasesApiHarness;

namespace Modules.Purchases.IntegrationTests;

/// <summary>Alta, edición, borrado y vista previa de órdenes de compra por HTTP, con un tenant nuevo por prueba.</summary>
public sealed class PurchaseOrderWriteApiTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task CreateReturns201WithLocationNumberAndADraftDetail()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.PostAsJsonAsync(
            OrderUrl(scenario.TenantId), OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/tenants/{scenario.TenantId}/purchases/orders/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("OC-0001", root.GetProperty("number").GetString());
        Assert.Equal("Draft", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt64());
        Assert.Equal(MemberName, root.GetProperty("createdBy").GetProperty("name").GetString());
        var line = Assert.Single(root.GetProperty("lines").EnumerateArray());
        Assert.Equal("SKU-A", line.GetProperty("code").GetString());
        Assert.Equal("Camiseta", line.GetProperty("name").GetString());
        Assert.Equal("Pending", line.GetProperty("lineStatus").GetString());
        Assert.Equal(
            "edit,delete,send,cancel",
            string.Join(',', root.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString())));
    }

    [Fact]
    public async Task CreateSnapshotsTheTaxRateAndAddsTaxOnTopOfTheCost()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)));

        Assert.Equal(19m, Assert.Single(order.Lines).TaxRatePercent);
        Assert.Equal(new PurchaseOrderTotalsDto(200m, 38m, 238m), order.Totals);
        Assert.Equal(238m, order.RequestedTotal);
    }

    // Review Focus 1: el candado del contador serializa dos altas simultáneas.
    [Fact]
    public async Task TwoConcurrentCreatesGetConsecutiveNumbers()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 1m, 10m));

        var orders = await Task.WhenAll(
            CreateOrderAsync(scenario.Manager, scenario.TenantId, body),
            CreateOrderAsync(scenario.Manager, scenario.TenantId, body));

        Assert.Equal(["OC-0001", "OC-0002"], orders.Select(order => order.Number).Order().ToArray());
    }

    // Review Focus 2: un alta rechazada no gasta el consecutivo.
    [Fact]
    public async Task ARejectedCreateDoesNotSpendANumber()
    {
        using var scenario = await NewScenarioAsync(Factory);
        using var other = await NewScenarioAsync(Factory);

        var rejected = await scenario.Manager.PostAsJsonAsync(
            OrderUrl(scenario.TenantId), OrderBody(scenario.SupplierId, (other.VariantA, 1m, 10m)), Ct);
        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 1m, 10m)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("purchases.order.line_not_in_catalog", await CodeOfAsync(rejected));
        Assert.Equal("OC-0001", order.Number);
    }

    [Fact]
    public async Task CreateWithoutLinesIs422Empty()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.PostAsJsonAsync(OrderUrl(scenario.TenantId), OrderBody(scenario.SupplierId), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchases.order.empty", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreateWithAnUnknownVariantIs422LineNotInCatalog()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.PostAsJsonAsync(
            OrderUrl(scenario.TenantId), OrderBody(scenario.SupplierId, (Guid.CreateVersion7(), 1m, 10m)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchases.order.line_not_in_catalog", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreateWithAnInactiveSupplierIs422SupplierInactive()
    {
        using var scenario = await NewScenarioAsync(Factory);
        await DeactivateSupplierAsync(scenario.Manager, scenario.TenantId, scenario.SupplierId);

        var response = await scenario.Manager.PostAsJsonAsync(
            OrderUrl(scenario.TenantId), OrderBody(scenario.SupplierId, (scenario.VariantA, 1m, 10m)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchases.order.supplier_inactive", await CodeOfAsync(response));
    }

    // D-3: el código es el de Suppliers.
    [Fact]
    public async Task CreateWithAnUnknownSupplierIs404SupplierNotFound()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.PostAsJsonAsync(
            OrderUrl(scenario.TenantId), OrderBody(Guid.CreateVersion7(), (scenario.VariantA, 1m, 10m)), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("suppliers.supplier.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreateWithAnUnsupportedCurrencyIs422InvalidCurrency()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.PostAsJsonAsync(
            OrderUrl(scenario.TenantId), OrderBodyIn(scenario.SupplierId, "XXX", (scenario.VariantA, 1m, 10m)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchases.order.invalid_currency", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreateWithZeroQuantityOrCostIs422WithTheFieldMap()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.PostAsJsonAsync(
            OrderUrl(scenario.TenantId),
            OrderBody(scenario.SupplierId, (scenario.VariantA, 0m, 10m), (scenario.VariantB, 1m, 0m)),
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        var keys = await ErrorKeysAsync(response);
        Assert.Contains("Lines[0].Quantity", keys);
        Assert.Contains("Lines[1].UnitCost", keys);
    }

    [Fact]
    public async Task UpdateReplacesTheDraftAndBumpsTheVersion()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)));

        var response = await WriteAsync(
            scenario.Manager,
            HttpMethod.Put,
            OrderUrl(scenario.TenantId, order.Id),
            order.Version,
            OrderBody(scenario.SupplierId, (scenario.VariantB, 3m, 50m), (scenario.VariantC, 1m, 10m)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
        Assert.Equal(2, updated.Version);
        Assert.Equal(["SKU-B", "SKU-C"], updated.Lines.Select(line => line.Code).ToArray());
        var read = await GetOrderAsync(scenario.Manager, scenario.TenantId, order.Id);
        Assert.Equal(2, read.Version);
        Assert.Equal(["SKU-B", "SKU-C"], read.Lines.Select(line => line.Code).ToArray());
    }

    // Review Focus 3: la tasa es una foto; sólo guardar el borrador vuelve a leer el catálogo.
    [Fact]
    public async Task TheLineKeepsItsTaxRateUntilTheDraftIsSaved()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m));
        var order = await CreateOrderAsync(scenario.Manager, scenario.TenantId, body);

        await ExecuteAsync(Factory, $"UPDATE catalog.tax_rates SET percentage = 5 WHERE id = {scenario.TaxRateId}");
        var read = await GetOrderAsync(scenario.Manager, scenario.TenantId, order.Id);
        var response = await WriteAsync(
            scenario.Manager, HttpMethod.Put, OrderUrl(scenario.TenantId, order.Id), order.Version, body);

        Assert.Equal(19m, Assert.Single(read.Lines).TaxRatePercent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
        Assert.Equal(5m, Assert.Single(saved.Lines).TaxRatePercent);
        Assert.Equal(new PurchaseOrderTotalsDto(200m, 10m, 210m), saved.Totals);
    }

    [Fact]
    public async Task UpdateWithAStaleIfMatchIs412AndWithoutItIs428()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m));
        var order = await CreateOrderAsync(scenario.Manager, scenario.TenantId, body);
        var url = OrderUrl(scenario.TenantId, order.Id);

        var stale = await WriteAsync(scenario.Manager, HttpMethod.Put, url, order.Version + 1, body);
        var missing = await WriteAsync(scenario.Manager, HttpMethod.Put, url, null, body);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(missing));
    }

    [Fact]
    public async Task UpdateOrDeleteOutsideDraftIs409NotDraft()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m));
        var sent = await TransitionAsync(
            scenario.Manager, scenario.TenantId, await CreateOrderAsync(scenario.Manager, scenario.TenantId, body), "send");
        var url = OrderUrl(scenario.TenantId, sent.Id);

        var update = await WriteAsync(scenario.Manager, HttpMethod.Put, url, sent.Version, body);
        var delete = await WriteAsync(scenario.Manager, HttpMethod.Delete, url, sent.Version);

        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        Assert.Equal("purchases.order.not_draft", await CodeOfAsync(update));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("purchases.order.not_draft", await CodeOfAsync(delete));
    }

    [Fact]
    public async Task DeleteADraftIs204AndThenGetIs404NotFound()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)));
        var url = OrderUrl(scenario.TenantId, order.Id);

        var delete = await WriteAsync(scenario.Manager, HttpMethod.Delete, url, order.Version);
        var get = await scenario.Manager.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("purchases.order.not_found", await CodeOfAsync(get));
    }

    [Fact]
    public async Task EveryWriteIsAudited()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m));
        var order = await CreateOrderAsync(scenario.Manager, scenario.TenantId, body);
        var url = OrderUrl(scenario.TenantId, order.Id);
        var updated = await WriteAsync(scenario.Manager, HttpMethod.Put, url, order.Version, body);
        var deleted = await WriteAsync(scenario.Manager, HttpMethod.Delete, url, order.Version + 1);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var resourceId = order.Id.ToString();
        var actions = await RowsAsync<string>(
            Factory,
            $"""SELECT action AS "Value" FROM audit.entries WHERE resource_id = {resourceId} ORDER BY action""");

        Assert.Equal(["purchases.order.created", "purchases.order.deleted", "purchases.order.updated"], actions.ToArray());
    }

    [Fact]
    public async Task PreviewMatchesTheTotalsTheCreateWouldStore()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 3m, 33.33m), (scenario.VariantC, 2m, 10.5m));

        var response = await scenario.Manager.PostAsJsonAsync($"{OrderUrl(scenario.TenantId)}/preview", body, Ct);
        var created = await CreateOrderAsync(scenario.Manager, scenario.TenantId, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<PurchaseOrderPreviewDto>(Ct))!;
        Assert.Equal(created.Totals, preview.Totals);
        Assert.Equal(
            [
                new PurchaseOrderPreviewLineDto(scenario.VariantA, "SKU-A", "Camiseta", 19m, 99.99m),
                new PurchaseOrderPreviewLineDto(scenario.VariantC, "SKU-C", "Gorra", 0m, 21m),
            ],
            preview.Lines.ToArray());
    }

    [Fact]
    public async Task PreviewWritesNothing()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m));

        var preview = await scenario.Manager.PostAsJsonAsync($"{OrderUrl(scenario.TenantId)}/preview", body, Ct);
        var list = await scenario.Manager.GetFromJsonAsync<PurchaseOrdersResponse>(OrderUrl(scenario.TenantId), Ct);
        var tenantId = scenario.TenantId;
        var audits = await ScalarAsync<long>(
            Factory,
            $"""SELECT count(*) AS "Value" FROM audit.entries WHERE tenant_id = {tenantId} AND action LIKE 'purchases.%'""");
        var counters = await ScalarAsync<long>(
            Factory, $"""SELECT count(*) AS "Value" FROM purchases.order_number_counters WHERE tenant_id = {tenantId}""");

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(0, list!.Total);
        Assert.Equal(0, audits);
        Assert.Equal(0, counters);
        Assert.Equal("OC-0001", (await CreateOrderAsync(scenario.Manager, scenario.TenantId, body)).Number);
    }

    [Theory]
    [InlineData("empty", "purchases.order.empty")]
    [InlineData("foreign-variant", "purchases.order.line_not_in_catalog")]
    [InlineData("inactive-supplier", "purchases.order.supplier_inactive")]
    [InlineData("invalid-currency", "purchases.order.invalid_currency")]
    [InlineData("zero-quantity", "validation.failed")]
    public async Task PreviewRunsTheSameRulesAsCreate(string scenarioName, string expectedCode)
    {
        using var scenario = await NewScenarioAsync(Factory);
        var body = OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m));
        switch (scenarioName)
        {
            case "empty":
                body = OrderBody(scenario.SupplierId);
                break;
            case "foreign-variant":
                using (var other = await NewScenarioAsync(Factory))
                {
                    body = OrderBody(scenario.SupplierId, (other.VariantA, 1m, 10m));
                }

                break;
            case "inactive-supplier":
                await DeactivateSupplierAsync(scenario.Manager, scenario.TenantId, scenario.SupplierId);
                break;
            case "invalid-currency":
                body = OrderBodyIn(scenario.SupplierId, "XXX", (scenario.VariantA, 1m, 10m));
                break;
            case "zero-quantity":
                body = OrderBody(scenario.SupplierId, (scenario.VariantA, 0m, 10m));
                break;
        }

        var response = await scenario.Manager.PostAsJsonAsync($"{OrderUrl(scenario.TenantId)}/preview", body, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, await CodeOfAsync(response));
        if (scenarioName == "zero-quantity")
        {
            Assert.Contains("Lines[0].Quantity", await ErrorKeysAsync(response));
        }
    }

    [Fact]
    public async Task PreviewNeedsTheManagePermission()
    {
        using var scenario = await NewScenarioAsync(Factory);
        using var reader = CreateReader(Factory, scenario.Tenant);

        var response = await reader.PostAsJsonAsync(
            $"{OrderUrl(scenario.TenantId)}/preview", OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
