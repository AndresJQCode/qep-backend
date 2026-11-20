using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Purchases.Application;
using static Modules.Purchases.IntegrationTests.PurchasesApiHarness;

namespace Modules.Purchases.IntegrationTests;

/// <summary>La lista de órdenes: paginación, filtros, búsqueda y los conteos de las pestañas (D-18).</summary>
public sealed class PurchaseOrderListApiTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QepApiFactory Factory => host.Factory;

    private static Task<PurchaseOrderDetailDto> CreateAsync(PurchasesScenario scenario, Guid supplierId, decimal quantity = 1m) =>
        CreateOrderAsync(scenario.Manager, scenario.TenantId, OrderBody(supplierId, (scenario.VariantA, quantity, 100m)));

    private static async Task<PurchaseOrdersResponse> ListAsync(PurchasesScenario scenario, string query = "") =>
        (await scenario.Manager.GetFromJsonAsync<PurchaseOrdersResponse>($"{OrderUrl(scenario.TenantId)}{query}", Ct))!;

    private static string[] NumbersOf(PurchaseOrdersResponse response) =>
        response.Items.Select(item => item.Number).ToArray();

    [Fact]
    public async Task ListIsPagedNewestFirstWithTotal()
    {
        using var scenario = await NewScenarioAsync(Factory);
        for (var index = 0; index < 3; index++)
        {
            await CreateAsync(scenario, scenario.SupplierId);
        }

        var page = await ListAsync(scenario, "?pageSize=2");

        Assert.Equal(["OC-0003", "OC-0002"], NumbersOf(page));
        Assert.Equal((3, 1, 2), (page.Total, page.Page, page.PageSize));
    }

    [Fact]
    public async Task ListFiltersByStatusIgnoringCaseAndRejectsUnknown()
    {
        using var scenario = await NewScenarioAsync(Factory);
        await CreateAsync(scenario, scenario.SupplierId);
        await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateAsync(scenario, scenario.SupplierId), "send");

        var sent = await ListAsync(scenario, "?status=sent");
        var unknown = await scenario.Manager.GetAsync($"{OrderUrl(scenario.TenantId)}?status=paused", Ct);

        Assert.Equal(["OC-0002"], NumbersOf(sent));
        Assert.Equal(1, sent.Total);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(unknown));
        Assert.Contains("Status", await ErrorKeysAsync(unknown));
    }

    [Fact]
    public async Task ListFiltersBySupplierAndSearchesNumberAndSupplierName()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var other = await CreateSupplierAsync(scenario.Manager, scenario.TenantId, "Botones del Sur");
        await CreateAsync(scenario, scenario.SupplierId);
        await CreateAsync(scenario, other);
        await CreateAsync(scenario, scenario.SupplierId);

        var bySupplier = await ListAsync(scenario, $"?supplierId={other}");
        var byNumber = await ListAsync(scenario, "?search=0002");
        var byName = await ListAsync(scenario, "?search=andinos");

        Assert.Equal(["OC-0002"], NumbersOf(bySupplier));
        Assert.Equal(["OC-0002"], NumbersOf(byNumber));
        Assert.Equal(["OC-0003", "OC-0001"], NumbersOf(byName));
    }

    [Fact]
    public async Task ListSummaryCarriesSupplierTotalAndLineCount()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateOrderAsync(
            scenario.Manager,
            scenario.TenantId,
            OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m), (scenario.VariantC, 1m, 10m)));

        var item = Assert.Single((await ListAsync(scenario)).Items);

        Assert.Equal(new PurchaseOrderSupplierRefDto(scenario.SupplierId, "Textiles Andinos"), item.Supplier);
        Assert.Equal(order.Totals!.Total, item.Total);
        Assert.Equal(248m, item.Total);
        Assert.Equal(2, item.LineCount);
        Assert.Equal(("OC-0001", "Draft", "COP"), (item.Number, item.Status, item.Currency));
    }

    [Fact]
    public async Task ListCapsPageSizeAt200()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var page = await ListAsync(scenario, "?pageSize=500");

        Assert.Equal(200, page.PageSize);
    }

    [Fact]
    public async Task CountsIgnoreStatusButHonorTheOtherFilters()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var other = await CreateSupplierAsync(scenario.Manager, scenario.TenantId, "Botones del Sur");
        await CreateAsync(scenario, scenario.SupplierId);
        await CreateAsync(scenario, scenario.SupplierId);
        await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateAsync(scenario, scenario.SupplierId), "send");
        await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateAsync(scenario, scenario.SupplierId), "cancel");
        await CreateAsync(scenario, other);

        var page = await ListAsync(scenario, $"?status=Sent&supplierId={scenario.SupplierId}");

        Assert.Single(page.Items);
        Assert.Equal(new PurchaseOrderStatusCountsDto(2, 1, 0, 0, 1, 4), page.Counts);
    }

    [Fact]
    public async Task CountsComeWithEveryKeyInZeroForAnEmptyTenant()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.GetAsync(OrderUrl(scenario.TenantId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var counts = document.RootElement.GetProperty("counts")
            .EnumerateObject()
            .Select(property => (property.Name, property.Value.GetInt32()))
            .ToArray();
        Assert.Equal(
            [("Draft", 0), ("Sent", 0), ("Confirmed", 0), ("Received", 0), ("Cancelled", 0), ("all", 0)],
            counts);
    }
}
