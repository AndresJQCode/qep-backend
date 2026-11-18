using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Servicios en cotizaciones y pedidos (spec 2026-11-18 §3.4), de punta a punta: el servicio se crea por la API de
/// Catalog con <c>kind</c> y <c>salesUnit</c>, como lo haría una persona, y la línea los fija al nacer por el lookup
/// real de Bootstrapper. Lo que estas pruebas ven y las unitarias no: que el adaptador lea las columnas de Catalog, que
/// la línea las guarde en <c>quotation_items</c>/<c>order_items</c> y que la respuesta las devuelva por nombre.
/// </summary>
public sealed class LineKindApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    // Review Focus 3: el tipo existe antes de enviar. La foto del nombre no existe en borrador, y el borrador ya
    // necesita saber si vende un servicio (requiresShipping, retenciones).
    [Fact]
    public async Task ADraftLineCarriesTheServiceKindBeforeSending()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var service = await CreateServiceAsync(client, tenantId);
        var draft = await CreateQuotationAsync(client, tenantId, clientId);

        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{draft.Id}/items",
            new AddQuotationItemRequest(service, 2m),
            TestContext.Current.CancellationToken);

        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var reread = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{draft.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(reread);
        Assert.Equal("Draft", reread.Status);
        var line = Assert.Single(reread.Items);
        Assert.Equal(("Service", "Hour"), (line.Kind, line.SalesUnit));
    }

    // Review Focus 4: convertir copia el tipo de la línea, no el que el producto tenga hoy. El producto vuelve a
    // físico antes de convertir y el pedido sigue vendiendo un servicio.
    [Fact]
    public async Task ConversionCopiesTheLineKindNotTheCurrentProductKind()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var service = await CreateServiceAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, service);

        await MakePhysicalAsync(client, tenantId, service);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, created.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        var line = Assert.Single(detail.Items);
        Assert.Equal(("Service", "Hour"), (line.Kind, line.SalesUnit));
    }

    // El pedido manual precia contra el catálogo, y con el precio toma el tipo y la unidad.
    [Fact]
    public async Task AManualOrderLineTakesTheCatalogKind()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var service = await CreateServiceAsync(client, tenantId);
        var physical = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            OrdersUrl(tenantId),
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(service, 3m), new OrderEditItemRequest(physical, 1m)],
                []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            [("Service", "Hour"), ("Physical", "Unit")],
            detail.Items.OrderBy(item => item.Position).Select(item => (item.Kind, item.SalesUnit)));
    }

    // ---- Siembra por la API de Catalog ----

    private const string ServiceCode = "SOP-HORA";

    /// <summary>Un servicio por hora, sin opciones: su única variante lleva el id del producto.</summary>
    private static async Task<Guid> CreateServiceAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = "Soporte técnico",
                kind = "Service",
                salesUnit = "Hour",
                variants = new[] { new { code = ServiceCode, pricing = Pricing(80_000m, NoDiscountScales()) } },
            },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<ProductDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    /// <summary>Vuelve físico el servicio por el PUT de Catalog, con la misma variante, código y precio.</summary>
    private static async Task MakePhysicalAsync(HttpClient client, Guid tenantId, Guid productId)
    {
        var current = await client.GetFromJsonAsync<ProductDto>(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}", TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}",
            new
            {
                expectedVersion = current.Version,
                name = "Soporte técnico",
                kind = "Physical",
                salesUnit = "Unit",
                variants = new[] { new { id = productId, code = ServiceCode, pricing = Pricing(80_000m, NoDiscountScales()) } },
            },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
