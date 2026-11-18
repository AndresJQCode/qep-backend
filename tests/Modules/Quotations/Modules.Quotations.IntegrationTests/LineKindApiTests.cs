using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    // ---- requiresShipping (spec 2026-11-18 §5) ----

    // El detalle lo lee del agregado y el listado lo pregunta con un EXISTS: las dos respuestas tienen que coincidir.
    [Fact]
    public async Task ServicesOnlyOrderDoesNotRequireShippingInDetailAndList()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var service = await CreateServiceAsync(client, tenantId);
        var physical = await CreateProductWithScalesAsync(client, tenantId);
        var servicesOnly = await CreateManualOrderAsync(client, tenantId, clientId, service);
        var mixed = await CreateManualOrderAsync(client, tenantId, clientId, service, physical);

        var list = await client.GetFromJsonAsync<OrdersPageResponse>(
            OrdersUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.False(servicesOnly.Order.RequiresShipping);
        Assert.True(mixed.Order.RequiresShipping);
        Assert.NotNull(list);
        Assert.False(Assert.Single(list.Items, row => row.Id == servicesOnly.Order.Id).RequiresShipping);
        Assert.True(Assert.Single(list.Items, row => row.Id == mixed.Order.Id).RequiresShipping);
    }

    // Una cotización con una línea física y un servicio requiere envío; una vacía también (el estado de hoy); una de sólo
    // servicios no.
    [Fact]
    public async Task MixedQuotationRequiresShippingInList()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var service = await CreateServiceAsync(client, tenantId);
        var physical = await CreateProductWithScalesAsync(client, tenantId);
        var mixed = await CreateQuotationWithLinesAsync(client, tenantId, clientId, service, physical);
        var servicesOnly = await CreateQuotationWithLinesAsync(client, tenantId, clientId, service);
        var empty = await CreateQuotationAsync(client, tenantId, clientId);

        var list = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(list);
        Assert.Equal(
            [(mixed.Id, true), (servicesOnly.Id, false), (empty.Id, true)],
            new[] { mixed.Id, servicesOnly.Id, empty.Id }
                .Select(id => (id, Assert.Single(list.Items, row => row.Id == id).RequiresShipping)));
        Assert.Equal((true, false), (mixed.RequiresShipping, servicesOnly.RequiresShipping));
    }

    // Spec §5: la regla de la ciudad al facturar sólo aplica si el pedido se envía, y uno de sólo servicios nace con
    // ShippingDetails.None. Se «presta» (prepare) y se marca pagado sin 422, para un cliente sin ciudad.
    [Fact]
    public async Task ServicesOnlyOrderMarksPaidWithoutShippingCity()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        await SetTenantCustomerFieldPolicyAsync(factory, tenantId, false, false, false, true);
        var customerId = await CreateCustomerWithoutCityAsync(client, tenantId);
        var service = await CreateServiceAsync(client, tenantId);
        var order = await CreateManualOrderAsync(client, tenantId, customerId, service);

        var prepared = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Order.Id)}/prepare", content: null, TestContext.Current.CancellationToken);
        var paid = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Order.Id)}/mark-paid", content: null, TestContext.Current.CancellationToken);

        Assert.True(prepared.IsSuccessStatusCode, await prepared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(paid.IsSuccessStatusCode, await paid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var after = await ReadOrderAsync(paid);
        Assert.False(after.RequiresShipping);
        Assert.NotNull(after.PaidMarkedAt);
    }

    private static async Task<OrderDetailResponse> CreateManualOrderAsync(
        HttpClient client, Guid tenantId, Guid clientId, params Guid[] variantIds)
    {
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var response = await client.PostAsJsonAsync(
            OrdersUrl(tenantId),
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [.. variantIds.Select(variantId => new OrderEditItemRequest(variantId, 1m))],
                []),
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<QuotationResponse> CreateQuotationWithLinesAsync(
        HttpClient client, Guid tenantId, Guid clientId, params Guid[] variantIds)
    {
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        HttpResponseMessage? last = null;
        foreach (var variantId in variantIds)
        {
            last = await client.PostAsJsonAsync(
                $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
                new AddQuotationItemRequest(variantId, 1m),
                TestContext.Current.CancellationToken);
            Assert.True(last.IsSuccessStatusCode, await last.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var body = await last!.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>Un cliente completo sin ciudad, como el de <c>BilledCustomerFieldsApiTests</c>.</summary>
    private static async Task<Guid> CreateCustomerWithoutCityAsync(HttpClient client, Guid tenantId)
    {
        var classificationId = await CreateClassificationAsync(client, tenantId);
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers",
            new
            {
                name = "Verde Esencial S.A.S.",
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                phone = "310 935 2187",
                email = "compras@verde.co",
                address = "Calle 10 # 45-12",
                country = "CO",
                personType = (string?)null,
                classificationId,
                withRetention = false,
                vatSurplus = false,
            },
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("id").GetGuid();
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
