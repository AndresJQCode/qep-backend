using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Líneas de cotización y pedido por variante (spec 2026-10-20 §4), de punta a punta: el producto con
/// opciones se crea por la API de Catalog, como lo haría una persona, y la línea se pide por
/// <c>variantId</c>. Lo que estas pruebas ven y las unitarias no: que el lookup real de Bootstrapper
/// resuelva precio, escala, código y nombre visible de la variante contra Postgres, y que la línea
/// guarde las dos referencias.
/// </summary>
public sealed class QuotationVariantLinesApiTests
{
    private static readonly string[] Sizes = ["M", "L"];

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    // Dos tallas del mismo producto son dos líneas, cada una con su código, su nombre visible y su
    // precio, y las dos con el mismo producto.
    [Fact]
    public async Task TwoSizesOfTheSameProductAreTwoLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        await AddItemAsync(client, tenantId, quotation.Id, shirt.M, 1m);
        var updated = await ReadQuotationAsync(await AddItemAsync(client, tenantId, quotation.Id, shirt.L, 2m));

        Assert.Equal(
            [
                (shirt.M, shirt.ProductId, "CAM-M", "Camiseta – M", 30_000m, 1m),
                (shirt.L, shirt.ProductId, "CAM-L", "Camiseta – L", 32_000m, 2m),
            ],
            updated.Items
                .OrderBy(item => item.Position)
                .Select(item => (item.VariantId, item.ProductId, item.ProductCode, item.ProductName, item.UnitPrice, item.Quantity)));
    }

    // La misma variante dos veces sigue siendo el duplicado de siempre: mismo código de error, que es
    // contrato con el frontend.
    [Fact]
    public async Task TheSameVariantTwiceIsTheDuplicateProductError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        (await AddItemAsync(client, tenantId, quotation.Id, shirt.M, 1m)).EnsureSuccessStatusCode();

        var response = await AddItemAsync(client, tenantId, quotation.Id, shirt.M, 3m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.item.duplicate_product", await CodeOfAsync(response));
    }

    // La regla del conteo, contra el lookup real: una escala 12-999 sin agrupación no cuenta 6 M y 6 L
    // como 12. Las dos tallas tienen la misma escala, así que esto prueba la regla, no la clave: la
    // clave la amarra EachSizeIsPricedWithItsOwnScales.
    [Fact]
    public async Task SixMAndSixLDoNotReachADozenScaleWithoutGrouping()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId, DozenScales(multiple: 1, allowGrouping: false));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        await AddItemAsync(client, tenantId, quotation.Id, shirt.M, 6m);
        var updated = await ReadQuotationAsync(await AddItemAsync(client, tenantId, quotation.Id, shirt.L, 6m));

        Assert.All(updated.Items, item => Assert.Equal((0m, "Own"), (item.DiscountPercentage, item.DiscountOrigin)));
    }

    // Enfoque de revisión 5, la clave: M y L tienen escalas distintas (M un tramo de 12 al 10 %, L
    // ninguno). Con 12 de cada una, M se lleva el 10 % y L no. Si el lookup o la pasada buscaran las
    // escalas por producto, las dos tallas saldrían con el mismo descuento.
    [Fact]
    public async Task EachSizeIsPricedWithItsOwnScales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(
            client, tenantId, DozenScales(multiple: 1, allowGrouping: false), scalesL: NoDiscountScales());
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        await AddItemAsync(client, tenantId, quotation.Id, shirt.M, 12m);
        var updated = await ReadQuotationAsync(await AddItemAsync(client, tenantId, quotation.Id, shirt.L, 12m));

        Assert.Equal(
            [(shirt.M, 10m), (shirt.L, 0m)],
            updated.Items.OrderBy(item => item.Position).Select(item => (item.VariantId, item.DiscountPercentage)));
    }

    // La misma escala con agrupación sí suma entre las dos tallas: las dos cumplen el múltiplo de 6
    // solas y juntas llegan a 12, así que se llevan el 10 % del grupo.
    [Fact]
    public async Task SixMAndSixLReachADozenScaleThatGroups()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId, DozenScales(multiple: 6, allowGrouping: true));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        await AddItemAsync(client, tenantId, quotation.Id, shirt.M, 6m);
        var updated = await ReadQuotationAsync(await AddItemAsync(client, tenantId, quotation.Id, shirt.L, 6m));

        Assert.Equal(2, updated.Items.Count);
        Assert.All(updated.Items, item => Assert.Equal((10m, "Group"), (item.DiscountPercentage, item.DiscountOrigin)));
    }

    // La foto de una línea enviada es el nombre visible de la variante al enviarla, y no se recalcula
    // al renombrar el producto; un borrador con la misma variante sí muestra el nombre nuevo.
    [Fact]
    public async Task ASentLineKeepsTheVariantNameAfterTheProductIsRenamed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId);
        var sent = await CreateSentQuotationAsync(client, factory, tenantId, customerId, shirt.M);
        var draft = await CreateQuotationAsync(client, tenantId, customerId);
        (await AddItemAsync(client, tenantId, draft.Id, shirt.M, 1m)).EnsureSuccessStatusCode();

        await RenameShirtAsync(client, tenantId, shirt, "Camiseta polo");

        var sentItem = Assert.Single((await GetQuotationAsync(client, tenantId, sent.Id)).Items);
        Assert.Equal(("CAM-M", "Camiseta – M"), (sentItem.ProductCode, sentItem.ProductName));
        var draftItem = Assert.Single((await GetQuotationAsync(client, tenantId, draft.Id)).Items);
        Assert.Equal(("CAM-M", "Camiseta polo – M"), (draftItem.ProductCode, draftItem.ProductName));
    }

    // El pedido manual también vende por variante: dos tallas son dos líneas, con la foto de cada una.
    [Fact]
    public async Task AManualOrderWithTwoSizesHasTwoLines()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            OrdersUrl(tenantId),
            await ManualOrderAsync(client, tenantId, clientId, new OrderEditItemRequest(shirt.M, 2m), new OrderEditItemRequest(shirt.L, 3m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            [
                (shirt.M, shirt.ProductId, "CAM-M", "Camiseta – M", 2m),
                (shirt.L, shirt.ProductId, "CAM-L", "Camiseta – L", 3m),
            ],
            detail.Items
                .OrderBy(item => item.Position)
                .Select(item => (item.VariantId, item.ProductId, item.ProductCode, item.ProductName, item.Quantity)));
    }

    // Agregar al pedido una variante que ya tiene es el duplicado del pedido, con su código de
    // siempre; otra talla del mismo producto entra como línea nueva.
    [Fact]
    public async Task AddingAVariantTheOrderAlreadyHasIsTheDuplicateProductError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var shirt = await CreateShirtInTwoSizesAsync(client, tenantId);
        var created = await client.PostAsJsonAsync(
            OrdersUrl(tenantId),
            await ManualOrderAsync(client, tenantId, clientId, new OrderEditItemRequest(shirt.M, 2m)),
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var order = await created.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);

        var duplicate = await client.PostAsJsonAsync(
            $"{OrdersUrl(tenantId)}/{order.Order.Id}/items",
            new AddOrderItemsRequest([new OrderItemAdditionRequest(shirt.M, 1m)]),
            TestContext.Current.CancellationToken);
        var otherSize = await client.PostAsJsonAsync(
            $"{OrdersUrl(tenantId)}/{order.Order.Id}/items",
            new AddOrderItemsRequest([new OrderItemAdditionRequest(shirt.L, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal("order.item.duplicate_product", await CodeOfAsync(duplicate));
        Assert.True(otherSize.IsSuccessStatusCode, await otherSize.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // ---- Siembra por la API de Catalog ----

    private sealed record Shirt(Guid ProductId, Guid M, Guid L, Guid OptionId, Guid ValueM, Guid ValueL);

    private sealed record OptionValueDto(Guid Id, string Name);

    private sealed record OptionDto(Guid Id, IReadOnlyList<OptionValueDto> Values);

    private sealed record VariantDto(Guid Id, string Code, string Name);

    private sealed record ProductDto(Guid Id, long Version, IReadOnlyList<VariantDto> Variants);

    private static object[] NoDiscountScales() =>
    [
        new { fromUnit = 1, toUnit = 999_999, discount = 0m, restriction = "multiple", multiple = 1 }
    ];

    private static object[] DozenScales(int multiple, bool allowGrouping) =>
    [
        new { fromUnit = 1, toUnit = 11, discount = 0m, restriction = "multiple", multiple = 1 },
        new { fromUnit = 12, toUnit = 999, discount = 10m, restriction = "multiple", multiple, allowGrouping }
    ];

    private static object Pricing(decimal cop, object[] scales) => new
    {
        prices = new Dictionary<string, decimal> { ["COP"] = cop },
        scales,
        packagingUnits = Array.Empty<int>()
    };

    /// <summary>«Camiseta» con la opción «Talla» M y L: CAM-M a 30.000 y CAM-L a 32.000 COP, con las
    /// mismas escalas salvo que <paramref name="scalesL"/> le dé a L las suyas.</summary>
    private static async Task<Shirt> CreateShirtInTwoSizesAsync(
        HttpClient client, Guid tenantId, object[]? scales = null, object[]? scalesL = null)
    {
        scales ??= NoDiscountScales();
        scalesL ??= scales;
        var optionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/options",
            new { name = "Talla", values = Sizes },
            TestContext.Current.CancellationToken);
        Assert.True(optionResponse.IsSuccessStatusCode, await optionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var option = await optionResponse.Content.ReadFromJsonAsync<OptionDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(option);
        var valueM = option.Values.Single(value => value.Name == "M").Id;
        var valueL = option.Values.Single(value => value.Name == "L").Id;

        var productResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = "Camiseta",
                optionIds = new[] { option.Id },
                variants = new object[]
                {
                    new { code = "CAM-M", optionValueIds = new[] { valueM }, pricing = Pricing(30_000m, scales) },
                    new { code = "CAM-L", optionValueIds = new[] { valueL }, pricing = Pricing(32_000m, scalesL) },
                }
            },
            TestContext.Current.CancellationToken);
        Assert.True(productResponse.IsSuccessStatusCode, await productResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var product = await productResponse.Content.ReadFromJsonAsync<ProductDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(product);

        return new Shirt(
            product.Id,
            product.Variants.Single(variant => variant.Code == "CAM-M").Id,
            product.Variants.Single(variant => variant.Code == "CAM-L").Id,
            option.Id,
            valueM,
            valueL);
    }

    /// <summary>Renombra el producto por el PUT de Catalog, con las mismas variantes, códigos y precios:
    /// lo único que cambia es el nombre, y con él el nombre visible de cada variante.</summary>
    private static async Task RenameShirtAsync(HttpClient client, Guid tenantId, Shirt shirt, string name)
    {
        var current = await client.GetFromJsonAsync<ProductDto>(
            $"/api/v1/tenants/{tenantId}/catalog/products/{shirt.ProductId}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{shirt.ProductId}",
            new
            {
                expectedVersion = current.Version,
                name,
                optionIds = new[] { shirt.OptionId },
                variants = new object[]
                {
                    new { id = shirt.M, code = "CAM-M", optionValueIds = new[] { shirt.ValueM }, pricing = Pricing(30_000m, NoDiscountScales()) },
                    new { id = shirt.L, code = "CAM-L", optionValueIds = new[] { shirt.ValueL }, pricing = Pricing(32_000m, NoDiscountScales()) },
                }
            },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // ---- Cotizaciones y pedidos ----

    private static Task<HttpResponseMessage> AddItemAsync(
        HttpClient client, Guid tenantId, Guid quotationId, Guid variantId, decimal quantity) =>
        client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/items",
            new AddQuotationItemRequest(variantId, quantity),
            TestContext.Current.CancellationToken);

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<QuotationResponse> GetQuotationAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var body = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}", TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<CreateOrderRequest> ManualOrderAsync(
        HttpClient client, Guid tenantId, Guid clientId, params OrderEditItemRequest[] items)
    {
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        return new CreateOrderRequest(
            clientId, "Transferencia", null, false, null, null,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            items, "PaymentPending", []);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("code").GetString();
    }
}
