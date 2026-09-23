using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El descuento global por escala: el asesor elige un piso (<c>FromUnit</c>) y cada linea toma el
/// tramo de su propio producto que arranca ahi, si mejora lo que ya tenia.
///
/// Las reglas de resolucion ya las cubre <c>QuotationScaleGroupPricingTests</c> en memoria. Lo
/// que este archivo verifica es lo que esas no pueden ver: que el piso se persista, que
/// sobreviva la conversion a pedido, que el composer arme los pisos disponibles contra el
/// catalogo real, y que el mismo endpoint sirva a la pantalla de cotizacion y a la de pedido.
/// </summary>
public sealed class GlobalScaleDiscountApiTests
{
    // Las escalas por defecto del harness van de a 1, asi que el multiplo nunca estorba: lo que
    // se ejerce aca es el piso, no la restriccion.
    private static string GlobalScaleUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/global-scale";

    [Fact]
    public async Task SettingTheGlobalFloorGivesItsDiscountToALineThatCannotReachItAlone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // 6 unidades caen en el tramo 1-9, que descuenta 0%, y no llegan solas al que arranca
        // en 20. Seis y no tres: la compuerta de compra minima pide 6 unidades o 500.000, y por
        // debajo de eso barre todos los descuentos -- incluido el global.
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 6m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(20, updated.GlobalScaleFloor);

        var item = Assert.Single(updated.Items);
        Assert.Equal(10m, item.DiscountPercentage);
        Assert.Equal("GlobalFloor", item.DiscountOrigin);
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
        var empty = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(empty);
        Assert.Empty(empty.AvailableGlobalScaleFloors);

        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 3m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var withItem = await added.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);

        // Los tres pisos de DefaultScales, ordenados y sin repetir. Llegan en la respuesta de la
        // mutacion y no solo en el GET: el frontend cachea lo que devuelve cada escritura.
        Assert.NotNull(withItem);
        Assert.Equal([1, 10, 20], withItem.AvailableGlobalScaleFloors);
    }

    [Fact]
    public async Task ClearingTheGlobalFloorPutsEveryLineBackOnItsOwnDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 6m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var applied = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);
        applied.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Null(updated.GlobalScaleFloor);

        var item = Assert.Single(updated.Items);
        Assert.Equal(0m, item.DiscountPercentage);
        Assert.Equal("Own", item.DiscountOrigin);
    }

    // El piso llega de una lista que el propio backend acaba de dar, asi que uno que no esta es
    // un bug del cliente y se dice como tal.
    [Fact]
    public async Task AFloorThatNoProductOffersIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 3m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(7777),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.global_scale.floor_not_available", problem.Code);
    }

    // Un piso de cero o negativo no puede coincidir con ninguna escala: se corta como error de
    // forma, no como piso inexistente.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task AFloorBelowOneIsRejectedAsAValidationError(int floor)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(floor),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("validation.failed", problem.Code);
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
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var narrow = await CreateProductWithGapInScalesAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 6m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var applied = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);
        applied.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(narrow, 3m),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(20, updated.GlobalScaleFloor);

        var first = updated.Items.Single(item => item.ProductId == productId);
        Assert.Equal(10m, first.DiscountPercentage);
        Assert.Equal("GlobalFloor", first.DiscountOrigin);

        // El producto de escalas angostas no tiene tramo que arranque en 20, asi que no participa.
        var second = updated.Items.Single(item => item.ProductId == narrow);
        Assert.Equal(0m, second.DiscountPercentage);
        Assert.Equal("Own", second.DiscountOrigin);
    }

    // Mismo endpoint para las dos pantallas, igual que el editor de lineas: el pedido pendiente
    // corrige el piso sin que la cotizacion vuelva a ser editable.
    [Fact]
    public async Task ThePendingOrderCanStillChangeTheGlobalFloor()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId, paymentMethod: null);
        var applied = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);
        applied.EnsureSuccessStatusCode();

        var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();

        // El piso sobrevivio la conversion.
        var afterConversion = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(afterConversion);
        Assert.Equal(20, afterConversion.GlobalScaleFloor);

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(10, updated.GlobalScaleFloor);
    }

    // La compuerta de compra minima (6 unidades o 500.000) barre TODOS los descuentos cuando no
    // se alcanza, y el global no es la excepcion. Lo que esta prueba fija es que ademas vuelva el
    // origen a Own: una linea que conservara "GlobalFloor" con 0% le haria decir a la pantalla
    // "descuento global aplicado" sobre nada.
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
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 3m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);

        // La eleccion del asesor queda guardada: lo que la compuerta quita es el efecto, no la
        // decision. Al agregar mas unidades el descuento tiene que volver solo.
        Assert.Equal(20, updated.GlobalScaleFloor);

        var item = Assert.Single(updated.Items);
        Assert.Equal(0m, item.DiscountPercentage);
        Assert.Equal("Own", item.DiscountOrigin);
    }

    // El pedido que ya no esta Pending no admite cambiar el piso. Es la rama que nacio de colapsar
    // los dos endpoints en uno, y hasta la revision no la ejercia ninguna prueba.
    [Fact]
    public async Task AnOrderThatIsNoLongerPendingRejectsTheChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId, paymentMethod: null);
        var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);

        var cancelled = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}/cancel",
            new { reason = "El cliente se arrepintio" },
            TestContext.Current.CancellationToken);
        cancelled.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("order.order.not_pending", problem.Code);
    }

    // Una cotizacion anulada no es editable y no tiene pedido que la rescate: la otra rama de
    // rechazo, tambien sin cobertura hasta la revision.
    [Fact]
    public async Task AVoidedQuotationRejectsTheChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 6m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var voided = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);
        voided.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.quotation.not_editable", problem.Code);
    }

    // El rastro: entrada de historial legible y evento de auditoria en la misma unidad de trabajo.
    // Una prueba que solo mira el status HTTP deja pasar el efecto que importa.
    [Fact]
    public async Task TheChangeLeavesHistoryAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 6m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();

        var applied = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);
        applied.EnsureSuccessStatusCode();

        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/history",
            TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        var entry = Assert.Single(
            history.Items,
            item => item.Details is not null
                && item.Details.Contains("descuento global", StringComparison.Ordinal));
        Assert.Equal("Edited", entry.EventType);
        Assert.Equal("Aplicó el descuento global de la escala desde 20 unidades.", entry.Details);

        // El EventName del outbox es el del sobre de auditoria; la accion va adentro del payload.
        var audits = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        Assert.Contains(audits, audit =>
        {
            using var entry = JsonDocument.Parse(audit.PayloadJson);
            return entry.RootElement.GetProperty("action").GetString()
                    == "quotation.quotation.global_scale_changed"
                && entry.RootElement.GetProperty("resourceId").GetString()
                    == quotation.Id.ToString();
        });
    }

    private sealed record ProblemPayload(string Code);
}
