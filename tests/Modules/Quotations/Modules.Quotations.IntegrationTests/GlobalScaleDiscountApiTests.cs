using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El descuento global por escala: el asesor elige un piso (<c>FromUnit</c>) y cada linea toma el
/// tramo de su propio producto que arranca ahi, si mejora lo que ya tenia.
///
/// **No hay un endpoint propio** (decision del owner, 2026-09-23). El piso viaja en el mismo
/// cuerpo que el resto del encabezado: la vista previa lo calcula al vuelo sin escribir nada,
/// igual que cambiar una cantidad, y el guardado lo persiste con "Guardar cambios". Para el
/// pedido pendiente, lo mismo con su propia vista previa y su propio guardado.
///
/// Las reglas de resolucion las cubre <c>QuotationScaleGroupPricingTests</c> en memoria. Lo que
/// este archivo verifica es lo que esas no pueden ver: que la vista previa no persista, que el
/// guardado si, que sobreviva la conversion, y que el composer arme los pisos disponibles contra
/// el catalogo real.
/// </summary>
public sealed class GlobalScaleDiscountApiTests
{
    private static string QuotationUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}";

    private static string OrderUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    // Lo que hace la pantalla con una vista previa: calcular, no escribir.
    [Fact]
    public async Task ThePreviewAppliesTheGlobalFloorWithoutSavingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);

        var preview = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/preview",
            RequestFor(quotation, globalScaleFloor: 20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var calculated = await ReadQuotationAsync(preview);
        Assert.Equal(20, calculated.GlobalScaleFloor);
        var line = Assert.Single(calculated.Items);
        Assert.Equal(10m, line.DiscountPercentage);
        Assert.Equal("GlobalFloor", line.DiscountOrigin);

        // Y no escribio nada: la cotizacion guardada sigue sin piso y sin descuento.
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Null(stored.GlobalScaleFloor);
        Assert.Equal(0m, Assert.Single(stored.Items).DiscountPercentage);
        Assert.Equal(quotation.Version, stored.Version);
    }

    [Fact]
    public async Task SavingPersistsTheGlobalFloor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);

        var response = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, globalScaleFloor: 20));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Equal(20, stored.GlobalScaleFloor);
        var line = Assert.Single(stored.Items);
        Assert.Equal(10m, line.DiscountPercentage);
        Assert.Equal("GlobalFloor", line.DiscountOrigin);
    }

    [Fact]
    public async Task TheAvailableFloorsComeFromTheProductsOfTheQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // Sin lineas no hay pisos que ofrecer, y viaja el arreglo vacio: una coleccion que
        // desaparece obliga a la pantalla a adivinar.
        var empty = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Empty(empty.AvailableGlobalScaleFloors);

        var added = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/items",
            new AddQuotationItemRequest(productId, 3m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var withItem = await ReadQuotationAsync(added);

        // Los tres pisos de DefaultScales, ordenados y sin repetir. Llegan en la respuesta de la
        // mutacion y no solo en el GET: el frontend cachea lo que devuelve cada escritura.
        Assert.Equal([1, 10, 20], withItem.AvailableGlobalScaleFloors);
    }

    // El guardado reemplaza el encabezado entero: un piso ausente es "quitalo".
    [Fact]
    public async Task SavingWithoutAFloorClearsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);
        var applied = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, globalScaleFloor: 20));
        applied.EnsureSuccessStatusCode();
        var withFloor = await ReadQuotationAsync(applied);

        var response = await PutQuotationAsync(
            client, tenantId, withFloor, RequestFor(withFloor, globalScaleFloor: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Null(stored.GlobalScaleFloor);
        var line = Assert.Single(stored.Items);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.Equal("Own", line.DiscountOrigin);
    }

    // Una escala nunca arranca por debajo de 1, asi que cero no coincide con ninguna. Es un
    // codigo de dominio y no un error de campo: el select no puede ofrecerlo, solo un cliente roto.
    [Fact]
    public async Task AFloorBelowOneIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);

        var response = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, globalScaleFloor: 0));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.global_scale.floor_invalid", (await ReadProblemAsync(response)).Code);
    }

    // Un piso que ningun producto ofrece se guarda y no aplica a nada. No se rechaza: el guardado
    // manda el piso en cada llamada, y rechazarlo haria que quitar la ultima linea que lo
    // respaldaba impidiera guardar -- el spec (seccion 5) quiere que la eleccion del asesor quede.
    [Fact]
    public async Task AFloorNoProductOffersIsKeptButAppliesToNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);

        var response = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, globalScaleFloor: 7777));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Equal(7777, stored.GlobalScaleFloor);
        Assert.Equal("Own", Assert.Single(stored.Items).DiscountOrigin);
    }

    // Agregar despues una linea de un producto sin ese tramo NO limpia el piso: limpiarlo seria
    // borrar una decision del asesor por un efecto colateral de otra accion.
    [Fact]
    public async Task AddingALineWithoutThatFloorLeavesTheChoiceInPlace()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var narrow = await CreateProductWithGapInScalesAsync(client, tenantId);
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);
        var applied = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, globalScaleFloor: 20));
        applied.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/items",
            new AddQuotationItemRequest(narrow, 3m),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var updated = await ReadQuotationAsync(response);
        Assert.Equal(20, updated.GlobalScaleFloor);
        var second = updated.Items.Single(item => item.ProductId == narrow);
        Assert.Equal(0m, second.DiscountPercentage);
        Assert.Equal("Own", second.DiscountOrigin);
    }

    // La pantalla del pedido pendiente: su vista previa calcula el piso al vuelo sin escribir.
    [Fact]
    public async Task TheOrderPreviewAppliesTheGlobalFloorWithoutSavingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order) = await PendingOrderAsync(client, factory, tenantId);

        var preview = await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, order.Id)}/preview",
            OrderRequestFor(quotation, globalScaleFloor: 20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var calculated = await ReadDetailAsync(preview);
        Assert.Equal(20, calculated.Quotation.GlobalScaleFloor);

        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Null(stored.GlobalScaleFloor);
    }

    [Fact]
    public async Task ThePendingOrderSavesTheGlobalFloor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order) = await PendingOrderAsync(client, factory, tenantId);

        var response = await PutOrderAsync(
            client, tenantId, order, OrderRequestFor(quotation, globalScaleFloor: 20));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(20, saved.Quotation.GlobalScaleFloor);
        // El corte "sin cambio real" del guardado del pedido miraba lineas, comprobantes y notas:
        // si no contara el piso, este guardado se habria descartado sin subir la version.
        Assert.True(saved.Order.Version > order.Version);
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        Assert.Equal(20, stored.GlobalScaleFloor);
    }

    [Fact]
    public async Task AVoidedQuotationRejectsTheChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);
        var voided = await client.PostAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/void",
            content: null,
            TestContext.Current.CancellationToken);
        voided.EnsureSuccessStatusCode();
        var current = await ReadQuotationAsync(voided);

        var response = await PutQuotationAsync(
            client, tenantId, current, RequestFor(current, globalScaleFloor: 20));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.quotation.not_editable", (await ReadProblemAsync(response)).Code);
    }

    // El rastro: sin esto, un guardado que solo cambia el piso no dejaba historial, porque la foto
    // del encabezado con la que se arma el resumen no tenia el piso.
    [Fact]
    public async Task SavingTheFloorLeavesHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithSixUnitsAsync(client, tenantId);

        var applied = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, globalScaleFloor: 20));
        applied.EnsureSuccessStatusCode();

        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationUrl(tenantId, quotation.Id)}/history",
            TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        Assert.Contains(
            history.Items,
            item => item.Details is not null
                && item.Details.Contains(
                    "descuento global (escala desde 20 unidades)", StringComparison.Ordinal));
    }

    // La compuerta de compra minima barre TODOS los descuentos cuando no se alcanza, el global
    // incluido, y el origen vuelve a Own: si no, la pantalla diria "descuento global" sobre un 0%.
    [Fact]
    public async Task BelowTheMinimumPurchaseTheGlobalDiscountIsSweptAwayOriginIncluded()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        // 3 unidades a 10.000 son 30.000: ni 6 unidades ni 500.000.
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 10_000m);
        var created = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, created.Id)}/items",
            new AddQuotationItemRequest(productId, 3m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var quotation = await ReadQuotationAsync(added);

        var response = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, globalScaleFloor: 20));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetQuotationAsync(client, tenantId, quotation.Id);
        // La eleccion del asesor queda: lo que la compuerta quita es el efecto, no la decision.
        Assert.Equal(20, stored.GlobalScaleFloor);
        var line = Assert.Single(stored.Items);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.Equal("Own", line.DiscountOrigin);
    }

    // 6 unidades caen en el tramo 1-9 (0%) y no llegan solas al que arranca en 20. Seis y no
    // tres: la compuerta de compra minima pide 6 unidades o 500.000, y por debajo barre todo.
    private static async Task<QuotationResponse> DraftWithSixUnitsAsync(
        HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var created = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, created.Id)}/items",
            new AddQuotationItemRequest(productId, 6m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        return await ReadQuotationAsync(added);
    }

    private static async Task<(QuotationResponse Quotation, OrderResponse Order)> PendingOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId, paymentMethod: null);
        var converted = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return (await GetQuotationAsync(client, tenantId, quotation.Id), order);
    }

    // El guardado reemplaza el encabezado entero: lo que no se prueba viaja como esta guardado,
    // incluida la cuenta de cobro, que si se omite se borra. Mismo criterio que
    // QuotationEditsApiTests.RequestFor.
    private static SaveQuotationRequest RequestFor(QuotationResponse quotation, int? globalScaleFloor) =>
        new(
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Notes,
            Parties: null,
            quotation.BillingAccount is { } billing
                ? new QuotationBillingAccountRequest(
                    billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency)
                : null,
            quotation.Items.Select(item => new QuotationEditItemRequest(item.ProductId, item.Quantity)).ToArray(),
            globalScaleFloor);

    // Las lineas del pedido son la lista entera deseada: se mandan las que ya estan para que el
    // guardado no las borre.
    private static SaveOrderEditsRequest OrderRequestFor(QuotationResponse quotation, int? globalScaleFloor) =>
        new(
            quotation.Items.Select(item => new OrderEditItemRequest(item.ProductId, item.Quantity)).ToArray(),
            new OrderEditProofsRequest(null, null, null),
            Notes: null,
            globalScaleFloor);

    private static async Task<HttpResponseMessage> PutQuotationAsync(
        HttpClient client, Guid tenantId, QuotationResponse quotation, SaveQuotationRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, QuotationUrl(tenantId, quotation.Id))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PutOrderAsync(
        HttpClient client, Guid tenantId, OrderResponse order, SaveOrderEditsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, OrderUrl(tenantId, order.Id))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{order.Version}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<QuotationResponse> ReadQuotationAsync(HttpResponseMessage response)
    {
        var quotation = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response)
    {
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<QuotationResponse> GetQuotationAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var quotation = await client.GetFromJsonAsync<QuotationResponse>(
            QuotationUrl(tenantId, quotationId), TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        return quotation;
    }

    private static async Task<ProblemPayload> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }

    private sealed record ProblemPayload(string Code);
}
