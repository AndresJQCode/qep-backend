using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Purchases.IntegrationTests.PurchasesApiHarness;

namespace Modules.Purchases.IntegrationTests;

/// <summary><c>GET /{id}/pdf</c> con el renderer falso del harness: nunca sale a <c>qcode-pdf</c>.</summary>
public sealed class PurchaseOrderPdfApiTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QepApiFactory Factory => host.Factory;

    private static string PdfUrl(Guid tenantId, Guid orderId) => $"{OrderUrl(tenantId, orderId)}/pdf";

    [Fact]
    public async Task PdfReturnsApplicationPdfWithTheNumberAsFileName()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)));
        using var reader = CreateReader(Factory, scenario.Tenant);

        var response = await reader.GetAsync(PdfUrl(scenario.TenantId, order.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("OC-0001.pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync(Ct)));
        var document = Factory.Services.GetRequiredService<FakePurchaseOrderPdfRenderer>().LastDocument!;
        Assert.Equal(
            ("Purchases API Tests", "OC-0001", "Borrador", "Textiles Andinos", "$ 238"),
            (document.TenantName, document.Number, document.StatusLabel, document.SupplierName, document.Total));
        Assert.StartsWith("NIT ", document.SupplierIdentification);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Cancelled")]
    public async Task PdfIsAvailableInEveryStatus(string status)
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)));
        if (status == "Cancelled")
        {
            order = await TransitionAsync(scenario.Manager, scenario.TenantId, order, "cancel");
        }

        var response = await scenario.Manager.GetAsync(PdfUrl(scenario.TenantId, order.Id), Ct);

        Assert.Equal(status, order.Status);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PdfOfAnotherTenantsRouteIs403()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m)));
        using var outsider = CreateManager(Factory, await NewTenantAsync(Factory));

        var response = await outsider.GetAsync(PdfUrl(scenario.TenantId, order.Id), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }

    [Fact]
    public async Task PdfOfAnUnknownOrderIs404()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var response = await scenario.Manager.GetAsync(PdfUrl(scenario.TenantId, Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("purchases.order.not_found", await CodeOfAsync(response));
    }
}
