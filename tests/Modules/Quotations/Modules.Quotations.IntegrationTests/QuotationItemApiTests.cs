using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    // no aplica y la linea se guarda sin descuento. El multiplo se cuenta DESDE FromUnit
    // (2026-09-21): una escala 5-48 de a 3 admite 5, 8, 11..., y 7 no.
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

    // El multiplo se cuenta desde FromUnit, no sobre la cantidad cruda: en 5-48 de a 3 cumple 8
    // (8 - 5 = 3), y 9 -- que es multiplo crudo de 3 y hasta el 2026-09-21 descontaba -- no.
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
            new AddQuotationItemRequest(productId, 8m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        var item = Assert.Single(created.Items);
        Assert.Equal(8m, item.Quantity);
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
    // cuando cae fuera del multiplo de la escala. Arranca en 8, que si cumple desde el piso, para
    // que se vea que el descuento se pierde al editar y no que nunca estuvo.
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
            new AddQuotationItemRequest(productId, 8m),
            TestContext.Current.CancellationToken));
        var itemId = Assert.Single(withItem.Items).Id;
        Assert.Equal(5m, Assert.Single(withItem.Items).DiscountPercentage);

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

    // La otra restriccion se cuenta sobre la cantidad cruda: empaques enteros de 12. Desde el
    // 2026-09-22 tampoco bloquea -- 20 unidades no son paquetes enteros de 12, asi que la linea
    // se guarda con la cantidad nueva y pierde el 15% que tenia con 24.
    [Fact]
    public async Task UpdateItemWithAPartialPackageIsAcceptedWithoutDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId, baseCop: 100_000m,
            scales: PackagesOfTwelve(100_000m, discount: 15m));
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var withItem = await ReadQuotationAsync(await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 24m),
            TestContext.Current.CancellationToken));
        var itemId = Assert.Single(withItem.Items).Id;
        Assert.Equal(15m, Assert.Single(withItem.Items).DiscountPercentage);

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
        Assert.Equal(0m, item.DiscountPercentage);
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

    // Un producto con escalas copiadas de otro queda incompleto y no se cotiza hasta que alguien
    // lo complete en el catálogo. Pasa por la cadena real: copia en Catalog, columna nullable en
    // Postgres, adaptador de Bootstrapper y el 422 del mapeo central.
    [Fact]
    public async Task AddItemRejectsAProductWithIncompleteScales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var source = await CreateProductWithScalesAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, scales: []);
        await CopyScalesAsync(client, tenantId, source, productId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.item.product_price_scales_incomplete", problem.Code);
    }

    // La cotización que ya tenía el producto se sigue leyendo —con la escala sin restricción—,
    // pero la línea no se puede volver a valorizar hasta completar el catálogo.
    [Fact]
    public async Task AQuotationWhoseProductLaterGotIncompleteScalesStaysReadable()
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
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken));
        var itemId = Assert.Single(withItem.Items).Id;

        var source = await CreateProductWithScalesAsync(client, tenantId);
        await CopyScalesAsync(client, tenantId, source, productId);

        var fetched = await ReadQuotationAsync(await client.GetAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken));
        var item = Assert.Single(fetched.Items);
        Assert.Equal(5m, item.DiscountPercentage);
        Assert.NotEmpty(item.PriceScales);
        Assert.All(item.PriceScales, scale => Assert.Null(scale.Restriction));

        var update = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(20m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
        var problem = await update.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.item.product_price_scales_incomplete", problem.Code);
    }

    // El contrato HTTP no se deriva del DTO interno: lo arma a mano
    // `QuotationResponseComposer`, asi que un campo agregado a `QuotationDto` no llega al
    // navegador hasta que alguien lo pase tambien a `QuotationResponse`. Eso fue exactamente lo
    // que paso con `minimumPurchase`, y la pantalla del editor murio leyendo `.met` de undefined
    // apenas la cotizacion tuvo lineas.
    //
    // Lee el JSON crudo a proposito, mismo criterio que `OrderContractApiTests`: deserializando
    // con el record de produccion la prueba quedaria en verde el dia que alguien vuelva a sacar
    // el campo del contrato, porque el record y la respuesta cambiarian juntos.
    [Fact]
    public async Task TheBatchResponseCarriesTheMinimumPurchaseToTheClient()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // 4 unidades cortan las dos ramas del minimo (6 unidades o $500.000), asi que los dos
        // faltantes viajan distintos de cero -- que es el caso que la pantalla dibuja. Con el
        // minimo alcanzado los tres campos serian 0/true y la prueba no distinguiria un mapeo
        // correcto de uno que devuelve el default del record.
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/batch",
            new BatchUpdateQuotationItemsRequest(
                [new BatchQuotationItemAdditionRequest(productId, 4m)],
                []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(
            body.RootElement.TryGetProperty("minimumPurchase", out var minimum),
            "La respuesta de la cotizacion no trae minimumPurchase.");
        Assert.Equal(JsonValueKind.Object, minimum.ValueKind);
        Assert.False(minimum.GetProperty("met").GetBoolean());
        Assert.Equal(4m, minimum.GetProperty("units").GetDecimal());
        Assert.Equal(6m, minimum.GetProperty("minimumUnits").GetDecimal());
        Assert.Equal(500_000m, minimum.GetProperty("minimumTotal").GetDecimal());
        Assert.Equal(2m, minimum.GetProperty("missingUnits").GetDecimal());
        // total = 4 x 100.000: la escala 1-9 no descuenta y el producto no tiene tasa (RN-013).
        Assert.Equal(100_000m, minimum.GetProperty("missingTotal").GetDecimal());
    }

    private static async Task CopyScalesAsync(
        HttpClient client, Guid tenantId, Guid sourceProductId, Guid targetProductId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/price-scales/copy",
            new { sourceProductId, targetProductIds = new[] { targetProductId } },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
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

    /// <summary>
    /// Una sola escala 1-999 por empaques de 12. Sin descuento salvo que se pida uno: los casos
    /// que solo ejercen la restriccion no lo necesitan, y el que verifica que una cantidad
    /// incompleta se guarda SIN descuento necesita que antes hubiera uno que perder.
    /// </summary>
    private static object[] PackagesOfTwelve(decimal baseCop, decimal discount = 0m) =>
    [
        new
        {
            fromUnit = 1, toUnit = 999, discount,
            restriction = "packaging_unit", packagingUnit = 12,
            finalCop = baseCop * (1m - discount / 100m)
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
