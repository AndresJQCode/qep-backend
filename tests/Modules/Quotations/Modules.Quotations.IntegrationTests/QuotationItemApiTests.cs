using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// US-3/US-4/US-5: agregar productos con descuento automatico por escala de cantidad, y ver los
/// totales recalcularse. Las reglas de calculo ya las cubren las unitarias de
/// <c>QuotationTests</c>/<c>QuotationDiscountResolverTests</c> contra el agregado/resolver en
/// memoria. Lo que este archivo verifica es lo que esas pruebas no pueden ver: que
/// <c>QuotationProductPricingLookup</c> de verdad resuelva el producto y sus escalas contra
/// Catalog a traves de HTTP real, y que <c>QuotationRepository</c> traiga/reemplace las lineas
/// contra Postgres.
/// </summary>
public sealed class QuotationItemApiTests
{
    [Fact]
    public async Task AddItemAppliesTheScaleDiscountAndRecalculatesTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // Cae en la escala 10-19 -> 5%.
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        var item = Assert.Single(updated.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(10m, item.Quantity);
        Assert.Equal(100_000m, item.UnitPrice);
        Assert.Equal(5m, item.DiscountPercentage);
        // gross = 1_000_000; discount 5% = 50_000; subtotal = 950_000.
        Assert.Equal(950_000m, item.Subtotal);
        Assert.Equal(950_000m, updated.Subtotal);
        // RN-013: sin tasa de impuesto asignada al producto, la linea cotiza con 0%.
        Assert.Equal(0, item.TaxPercentage);
        Assert.Equal(0m, updated.TaxAmount);
        Assert.Equal(950_000m, updated.Total);
    }

    // RN-013: el impuesto de la cotizacion es la suma del de cada linea, resuelto contra la
    // tasa de impuesto propia de cada producto -- no un unico porcentaje sobre el subtotal.
    [Fact]
    public async Task AddItemsWithDifferentTaxRatesSumTheirTaxIntoTheHeader()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var ivaGeneral = await CreateTaxRateAsync(client, tenantId, "IVA general", 19);
        var exento = await CreateTaxRateAsync(client, tenantId, "Exento", 0);
        // 119_000 con el 19% ya adentro -> base 100_000, igual que los dos sin impuesto, que
        // al no tener tasa son todos base.
        var taxedProductId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 119_000m, taxRateId: ivaGeneral);
        var exemptProductId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, taxRateId: exento);
        var untaxedProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);

        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(taxedProductId, 1m),
            TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(exemptProductId, 1m),
            TestContext.Current.CancellationToken);
        var final = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(untaxedProductId, 1m),
            TestContext.Current.CancellationToken));

        // Tres lineas con base 100_000 c/u: subtotal = 300_000. Sólo la primera trae IVA
        // adentro: 19_000 extraidos de sus 119_000 -- las otras dos aportan 0.
        Assert.Equal(300_000m, final.Subtotal);
        Assert.Equal(19_000m, final.TaxAmount);
        Assert.Equal(319_000m, final.Total);
        Assert.Equal(19, Assert.Single(final.Items, item => item.ProductId == taxedProductId).TaxPercentage);
        Assert.Equal(0, Assert.Single(final.Items, item => item.ProductId == exemptProductId).TaxPercentage);
        Assert.Equal(0, Assert.Single(final.Items, item => item.ProductId == untaxedProductId).TaxPercentage);
    }

    // Decision confirmada (§1.5 del modelo de datos): una cantidad que no cae en ninguna escala
    // definida da 0% -- no bloquea la linea.
    [Fact]
    public async Task AddItemWithQuantityOutsideAnyScaleAppliesZeroDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        // Solo cubre 10-19: pedir 3 unidades cae fuera de cualquier escala.
        var productId = await CreateProductWithGapInScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 3m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        var item = Assert.Single(updated.Items);
        Assert.Equal(0m, item.DiscountPercentage);
        Assert.Equal(300_000m, item.Subtotal);
    }

    [Fact]
    public async Task AddItemForAnUnknownProductIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(Guid.NewGuid(), 1m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AddItemWithZeroQuantityIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 0m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // US-4: la cantidad nueva puede caer en otra escala del mismo producto, asi que el descuento
    // se vuelve a resolver -- nunca se conserva el anterior.
    [Fact]
    public async Task UpdateItemQuantityReResolvesTheDiscountAndRecalculatesTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var withItem = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 5m),
            TestContext.Current.CancellationToken));
        var itemId = Assert.Single(withItem.Items).Id;
        Assert.Equal(0m, withItem.Items.Single().DiscountPercentage);

        // Sube a 20 unidades -> escala de 10%.
        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(20m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        var item = Assert.Single(updated.Items);
        Assert.Equal(20m, item.Quantity);
        Assert.Equal(10m, item.DiscountPercentage);
        // gross = 2_000_000; discount 10% = 200_000; subtotal = 1_800_000.
        Assert.Equal(1_800_000m, item.Subtotal);
        Assert.Equal(1_800_000m, updated.Subtotal);
    }

    [Fact]
    public async Task RemoveItemDropsTheLineAndRecalculatesTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var withItem = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 1m),
            TestContext.Current.CancellationToken));
        var itemId = Assert.Single(withItem.Items).Id;

        var response = await client.DeleteAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Empty(updated.Items);
        Assert.Equal(0m, updated.Subtotal);
        Assert.Equal(0m, updated.Total);

        // Releido desde la base, no solo desde la respuesta de la escritura -- eso probaria el
        // mapeo de salida, no si QuotationRepository de verdad borro la fila.
        var fetched = await ReadQuotationAsync(await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken));
        Assert.Empty(fetched.Items);
    }

    // CAT-09: Multiple ya no bloquea la linea -- si la cantidad no cae en el multiplo, la escala
    // no aplica y la linea se guarda sin descuento. El multiplo se cuenta sobre la cantidad
    // cruda: una escala 5-48 de a 3 admite 3, 6, 9..., y 7 no.
    [Fact]
    public async Task AddItemOffTheScaleMultipleIsAcceptedWithoutDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: MultipleOfThreeFromFive(100_000m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 7m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(0m, Assert.Single(created.Items).DiscountPercentage);
    }

    // El multiplo se cuenta sobre la cantidad cruda, no desde FromUnit: 9 = 3 x 3 cumple.
    [Fact]
    public async Task AddItemOnTheScaleMultipleIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: MultipleOfThreeFromFive(100_000m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 9m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        var item = Assert.Single(created.Items);
        Assert.Equal(9m, item.Quantity);
        Assert.Equal(5m, item.DiscountPercentage);
    }

    // La cantidad que no cae en ninguna escala sigue sin descuento y sin bloqueo (decision
    // confirmada): 2 esta por debajo del 5 donde arranca la unica escala del producto.
    [Fact]
    public async Task AddItemBelowEveryScaleIsNotRestricted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: MultipleOfThreeFromFive(100_000m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 2m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(0m, Assert.Single(created.Items).DiscountPercentage);
    }

    // Multiple ya no bloquea la edicion: la cantidad nueva se guarda igual, sin descuento,
    // cuando cae fuera del multiplo de la escala.
    [Fact]
    public async Task UpdateItemOffTheScaleMultipleIsAcceptedWithoutDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: MultipleOfThreeFromFive(100_000m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var withItem = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 9m),
            TestContext.Current.CancellationToken));
        var itemId = Assert.Single(withItem.Items).Id;

        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(7m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        var item = Assert.Single(updated.Items);
        Assert.Equal(7m, item.Quantity);
        Assert.Equal(0m, item.DiscountPercentage);
    }

    // La otra restriccion se cuenta sobre la cantidad cruda: empaques enteros de 12.
    [Fact]
    public async Task UpdateItemWithAPartialPackageIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: PackagesOfTwelve(100_000m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var withItem = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 24m),
            TestContext.Current.CancellationToken));
        var itemId = Assert.Single(withItem.Items).Id;

        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(20m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.item.quantity_not_packaging_unit", problem.Code);
    }

    // BFF: sin la restriccion en la respuesta, la pantalla no tiene con que evitar el 422 antes
    // de enviar -- solo con que reaccionar despues.
    [Fact]
    public async Task ItemPriceScalesCarryTheirRestrictionToTheClient()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m, scales: MultipleOfThreeFromFive(100_000m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var created = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 8m),
            TestContext.Current.CancellationToken));

        var scale = Assert.Single(Assert.Single(created.Items).PriceScales);
        Assert.Equal("multiple", scale.Restriction);
        Assert.Equal(3, scale.Multiple);
        Assert.Null(scale.PackagingUnit);
    }

    /// <summary>Una sola escala 5-48 al 5%, de a 3 desde 5.</summary>
    private static object[] MultipleOfThreeFromFive(decimal baseCop) =>
    [
        new
        {
            fromUnit = 5, toUnit = 48, discount = 5m,
            restriction = "multiple", multiple = 3, finalCop = baseCop * 0.95m
        }
    ];

    /// <summary>Una sola escala 1-999 sin descuento, solo por empaques de 12.</summary>
    private static object[] PackagesOfTwelve(decimal baseCop) =>
    [
        new
        {
            fromUnit = 1, toUnit = 999, discount = 0m,
            restriction = "packaging_unit", packagingUnit = 12, finalCop = baseCop
        }
    ];

    /// <summary>Las extensiones de ProblemDetails llegan aplanadas en la raiz
    /// (ApiExceptionHandler).</summary>
    private sealed record ProblemPayload(string Code);

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }
}
