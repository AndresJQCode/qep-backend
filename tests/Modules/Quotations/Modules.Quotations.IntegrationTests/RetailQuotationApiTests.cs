using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La cotizacion detal: el interruptor que deja a todas las lineas sin descuento, cualesquiera
/// sean las escalas de su producto y la cantidad pedida.
///
/// **No hay un endpoint propio** (decision del owner, 2026-09-23), igual que el piso global desde
/// 400773f: <c>isRetail</c> viaja en el mismo cuerpo que el resto del encabezado. La vista previa
/// lo calcula al vuelo sin escribir y el guardado lo persiste; para el pedido pendiente, lo mismo
/// con su propia vista previa y su propio guardado.
///
/// Que el corte funcione en memoria ya lo cubre <c>QuotationPricingRecalculationTests</c>, y la
/// exclusion con el piso sobre el agregado, <c>QuotationRetailTests</c>. Lo que este archivo
/// verifica es lo que esas no pueden ver: que el flag llegue desde el JSON, que se persista, que
/// la vista previa no escriba, que prenderlo limpie el piso guardado, que el pedido pendiente
/// tambien lo pueda cambiar, y que el rastro quede en historial y auditoria.
/// </summary>
public sealed class RetailQuotationApiTests
{
    private static string QuotationUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}";

    private static string OrderUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    [Fact]
    public async Task SavingWithRetailOnLeavesEveryLineWithoutDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var before = await DraftWithTenUnitsAsync(client, tenantId);
        Assert.False(before.IsRetail);
        Assert.Equal(5m, Assert.Single(before.Items).DiscountPercentage);

        var response = await PutQuotationAsync(
            client, tenantId, before, RequestFor(before, isRetail: true, globalScaleFloor: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadQuotationAsync(response);
        Assert.True(updated.IsRetail);
        var item = Assert.Single(updated.Items);
        Assert.Equal(0m, item.DiscountPercentage);
        Assert.Equal("Own", item.DiscountOrigin);
        // Sin descuento el total sube: 10 unidades a lista contra las mismas 10 con el 5%.
        Assert.True(updated.Total > before.Total);
        Assert.Equal(0m, updated.DiscountAmount);

        // Y se persistio: la lectura posterior lo trae igual.
        var stored = await GetQuotationAsync(client, tenantId, before.Id);
        Assert.True(stored.IsRetail);
        Assert.Equal(0m, Assert.Single(stored.Items).DiscountPercentage);
    }

    // Detal y piso global son excluyentes. Prender el detal limpia el piso en vez de guardarlo
    // dormido: una respuesta que siguiera publicando el piso le haria pintar a la pantalla un
    // descuento de escala que el recalculo esta ignorando.
    [Fact]
    public async Task TurningRetailOnClearsTheSavedFloorAndTurningItOffDoesNotRestoreIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var draft = await DraftWithTenUnitsAsync(client, tenantId);
        var withFloor = await PutQuotationAsync(
            client, tenantId, draft, RequestFor(draft, isRetail: false, globalScaleFloor: 20));
        withFloor.EnsureSuccessStatusCode();
        var floored = await ReadQuotationAsync(withFloor);
        Assert.Equal(20, floored.GlobalScaleFloor);

        var on = await PutQuotationAsync(
            client, tenantId, floored, RequestFor(floored, isRetail: true, globalScaleFloor: null));

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var retail = await ReadQuotationAsync(on);
        Assert.True(retail.IsRetail);
        Assert.Null(retail.GlobalScaleFloor);
        Assert.Equal(0m, Assert.Single(retail.Items).DiscountPercentage);

        // Y no vuelve al apagar: el piso es una eleccion del asesor, no un estado derivado.
        var off = await PutQuotationAsync(
            client, tenantId, retail, RequestFor(retail, isRetail: false, globalScaleFloor: null));
        off.EnsureSuccessStatusCode();
        var afterOff = await ReadQuotationAsync(off);
        Assert.False(afterOff.IsRetail);
        Assert.Null(afterOff.GlobalScaleFloor);
    }

    // Detal se aplica antes que el piso: apagarlo y elegir un piso en el mismo guardado funciona.
    [Fact]
    public async Task TurningRetailOffAndChoosingAFloorInTheSameSaveWorks()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var draft = await DraftWithTenUnitsAsync(client, tenantId);
        var on = await PutQuotationAsync(
            client, tenantId, draft, RequestFor(draft, isRetail: true, globalScaleFloor: null));
        on.EnsureSuccessStatusCode();
        var retail = await ReadQuotationAsync(on);

        var response = await PutQuotationAsync(
            client, tenantId, retail, RequestFor(retail, isRetail: false, globalScaleFloor: 20));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadQuotationAsync(response);
        Assert.False(saved.IsRetail);
        Assert.Equal(20, saved.GlobalScaleFloor);
        Assert.Equal("GlobalFloor", Assert.Single(saved.Items).DiscountOrigin);
    }

    // El cuerpo contradictorio: prender el detal y pedir un piso a la vez. No hay regla cruzada en
    // el validador de forma; lo resuelve el dominio con el mismo codigo que el piso suelto.
    [Fact]
    public async Task RetailOnWithAFloorInTheSameBodyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var draft = await DraftWithTenUnitsAsync(client, tenantId);

        var response = await PutQuotationAsync(
            client, tenantId, draft, RequestFor(draft, isRetail: true, globalScaleFloor: 20));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.retail.floor_not_allowed", (await ReadProblemAsync(response)).Code);

        // Rechazado entero: ni el detal ni el piso quedaron guardados.
        var stored = await GetQuotationAsync(client, tenantId, draft.Id);
        Assert.False(stored.IsRetail);
        Assert.Null(stored.GlobalScaleFloor);
    }

    // Lo que hace la pantalla con una vista previa: calcular, no escribir. Es el punto de mover el
    // detal al borrador: el asesor ve el efecto sobre cada linea antes de guardar.
    [Fact]
    public async Task ThePreviewAppliesRetailWithoutSavingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var draft = await DraftWithTenUnitsAsync(client, tenantId);
        var withFloor = await PutQuotationAsync(
            client, tenantId, draft, RequestFor(draft, isRetail: false, globalScaleFloor: 20));
        withFloor.EnsureSuccessStatusCode();
        var floored = await ReadQuotationAsync(withFloor);

        var preview = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, floored.Id)}/preview",
            RequestFor(floored, isRetail: true, globalScaleFloor: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var calculated = await ReadQuotationAsync(preview);
        Assert.True(calculated.IsRetail);
        Assert.Null(calculated.GlobalScaleFloor);
        var line = Assert.Single(calculated.Items);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.Equal("Own", line.DiscountOrigin);

        // Y no escribio nada: la cotizacion guardada sigue sin detal, con su piso y su version.
        var stored = await GetQuotationAsync(client, tenantId, floored.Id);
        Assert.False(stored.IsRetail);
        Assert.Equal(20, stored.GlobalScaleFloor);
        Assert.Equal(floored.Version, stored.Version);
    }

    // La pantalla del pedido pendiente: su vista previa calcula el detal al vuelo sin escribir, y
    // su guardado lo persiste aunque la cotizacion ya no sea editable.
    [Fact]
    public async Task ThePendingOrderPreviewsAndSavesRetail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order) = await PendingOrderAsync(client, factory, tenantId);

        // Diez unidades para que haya un descuento que el detal pueda quitar: sin detal, el
        // mismo borrador toma el 5% del tramo 10-19.
        var withoutRetail = await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, order.Id)}/preview",
            OrderRequestFor(quotation, quantity: 10m, isRetail: false),
            TestContext.Current.CancellationToken);
        withoutRetail.EnsureSuccessStatusCode();
        Assert.Equal(
            5m, Assert.Single((await ReadDetailAsync(withoutRetail)).Quotation.Items).DiscountPercentage);

        var preview = await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, order.Id)}/preview",
            OrderRequestFor(quotation, quantity: 10m, isRetail: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var calculated = await ReadDetailAsync(preview);
        Assert.True(calculated.Quotation.IsRetail);
        var line = Assert.Single(calculated.Quotation.Items);
        Assert.Equal(0m, line.DiscountPercentage);
        Assert.Equal("Own", line.DiscountOrigin);
        Assert.False((await GetQuotationAsync(client, tenantId, quotation.Id)).IsRetail);

        // Solo el detal: sin lineas, comprobantes ni notas que cambien. El corte "sin cambio real"
        // del guardado tiene que contarlo, o se descartaria en silencio sin subir la version.
        var response = await PutOrderAsync(
            client, tenantId, order, OrderRequestFor(quotation, quantity: null, isRetail: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.True(saved.Quotation.IsRetail);
        Assert.True(saved.Order.Version > order.Version);
        Assert.True((await GetQuotationAsync(client, tenantId, quotation.Id)).IsRetail);
    }

    // El pedido que ya no esta Pending no admite cambiarlo: el mismo error que ya da su guardado.
    [Fact]
    public async Task AnOrderThatIsNoLongerPendingRejectsTheChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var (quotation, order) = await PendingOrderAsync(client, factory, tenantId);
        var cancelled = await client.PostAsJsonAsync(
            $"{OrderUrl(tenantId, order.Id)}/cancel",
            new { reason = "El cliente se arrepintio" },
            TestContext.Current.CancellationToken);
        cancelled.EnsureSuccessStatusCode();

        var response = await PutOrderAsync(
            client, tenantId, order, OrderRequestFor(quotation, quantity: null, isRetail: true));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_pending", (await ReadProblemAsync(response)).Code);
    }

    // Una cotizacion anulada no es editable y no tiene pedido que la rescate: el mismo error que
    // ya da el guardado.
    [Fact]
    public async Task AVoidedQuotationRejectsTheChange()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var draft = await DraftWithTenUnitsAsync(client, tenantId);
        var voided = await client.PostAsync(
            $"{QuotationUrl(tenantId, draft.Id)}/void",
            content: null,
            TestContext.Current.CancellationToken);
        voided.EnsureSuccessStatusCode();
        var current = await ReadQuotationAsync(voided);

        var response = await PutQuotationAsync(
            client, tenantId, current, RequestFor(current, isRetail: true, globalScaleFloor: null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.quotation.not_editable", (await ReadProblemAsync(response)).Code);
    }

    // El rastro del guardado de la cotizacion: el detal entra en la misma fila de "Editó ..." que
    // el resto del encabezado, igual que el piso. Una prueba que solo mira el status HTTP deja
    // pasar el efecto que importa.
    [Fact]
    public async Task SavingRetailOnTheQuotationLeavesHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var draft = await DraftWithTenUnitsAsync(client, tenantId);

        var applied = await PutQuotationAsync(
            client, tenantId, draft, RequestFor(draft, isRetail: true, globalScaleFloor: null));
        applied.EnsureSuccessStatusCode();

        var history = await GetHistoryAsync(client, tenantId, draft.Id);
        var entry = Assert.Single(
            history.Items,
            item => item.Details is not null
                && item.Details.Contains("detal", StringComparison.Ordinal));
        Assert.Equal("Edited", entry.EventType);
        Assert.Equal("Editó detal (ninguna línea recibe descuento).", entry.Details);

        // Guardar otra vez con el mismo detal no es un cambio: no deja otra fila.
        var saved = await ReadQuotationAsync(applied);
        var again = await PutQuotationAsync(
            client, tenantId, saved, RequestFor(saved, isRetail: true, globalScaleFloor: null));
        again.EnsureSuccessStatusCode();
        var afterAgain = await GetHistoryAsync(client, tenantId, draft.Id);
        Assert.Single(
            afterAgain.Items,
            item => item.Details is not null
                && item.Details.Contains("detal", StringComparison.Ordinal));
    }

    // El rastro del guardado del pedido: fila propia con el texto de RetailChanged y auditoria
    // sobre el pedido, igual que el piso en ese mismo guardado.
    [Fact]
    public async Task SavingRetailOnThePendingOrderLeavesHistoryAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order) = await PendingOrderAsync(client, factory, tenantId);

        var applied = await PutOrderAsync(
            client, tenantId, order, OrderRequestFor(quotation, quantity: null, isRetail: true));
        applied.EnsureSuccessStatusCode();

        var history = await GetHistoryAsync(client, tenantId, quotation.Id);
        var entry = Assert.Single(
            history.Items,
            item => item.Details is not null
                && item.Details.Contains("detal", StringComparison.Ordinal));
        Assert.Equal("Edited", entry.EventType);
        Assert.Equal(
            "Marcó la cotización como detal: ninguna línea recibe descuento.", entry.Details);

        // El EventName del outbox es el del sobre de auditoria; la accion va adentro del payload.
        var audits = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        Assert.Contains(audits, audit =>
        {
            using var recorded = JsonDocument.Parse(audit.PayloadJson);
            return recorded.RootElement.GetProperty("action").GetString()
                    == "quotation.order.retail_changed"
                && recorded.RootElement.GetProperty("resourceId").GetString()
                    == order.Id.ToString();
        });
    }

    // 10 unidades caen en el tramo 10-19 de DefaultScales, que descuenta 5%, y 1.000.000 pasa la
    // compra minima de sobra: el descuento es real antes de prender el detal, asi que lo que cada
    // prueba mide es el interruptor y no la compuerta.
    // "No validar restricciones" (decision del owner, 2026-09-23): en detal las escalas no se
    // usan, asi que un producto con escalas a medio configurar se cotiza igual. Sin detal, el
    // mismo producto da 422 quotation.item.product_price_scales_incomplete
    // (QuotationItemApiTests.AddItemRejectsAProductWithIncompleteScales).
    [Fact]
    public async Task ARetailQuotationAcceptsAProductWithIncompleteScales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithTenUnitsAsync(client, tenantId);
        var retail = await PutQuotationAsync(
            client, tenantId, quotation, RequestFor(quotation, isRetail: true, globalScaleFloor: null));
        retail.EnsureSuccessStatusCode();
        var incomplete = await ProductWithIncompleteScalesAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/items",
            new AddQuotationItemRequest(incomplete, 4m),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var line = (await ReadQuotationAsync(response)).Items.Single(item => item.ProductId == incomplete);
        Assert.Equal(0m, line.DiscountPercentage);
    }

    // Prender detal y agregar el producto incompleto en el MISMO guardado del borrador: el
    // encabezado se aplica antes que las lineas, asi que las lineas ya se valorizan en detal.
    [Fact]
    public async Task TurningRetailOnAndAddingAProductWithIncompleteScalesInTheSameSaveWorks()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await DraftWithTenUnitsAsync(client, tenantId);
        var incomplete = await ProductWithIncompleteScalesAsync(client, tenantId);
        var body = RequestFor(quotation, isRetail: true, globalScaleFloor: null) with
        {
            Items =
            [
                .. quotation.Items.Select(item => new QuotationEditItemRequest(item.ProductId, item.Quantity)),
                new QuotationEditItemRequest(incomplete, 4m)
            ]
        };

        var response = await PutQuotationAsync(client, tenantId, quotation, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadQuotationAsync(response);
        Assert.True(saved.IsRetail);
        Assert.Contains(saved.Items, item => item.ProductId == incomplete);
        Assert.All(saved.Items, item => Assert.Equal(0m, item.DiscountPercentage));
    }

    // Lo mismo en el pedido pendiente, donde el detal se aplica DESPUES de las lineas: si las
    // lineas leyeran el detal de la cotizacion verian el viejo y darian 422. Se valorizan con el
    // detal del cuerpo, que es como va a quedar el pedido guardado.
    [Fact]
    public async Task ThePendingOrderTurnsRetailOnAndAddsAProductWithIncompleteScalesInTheSameSave()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order) = await PendingOrderAsync(client, factory, tenantId);
        var incomplete = await ProductWithIncompleteScalesAsync(client, tenantId);
        var body = OrderRequestFor(quotation, quantity: null, isRetail: true) with
        {
            Items =
            [
                .. quotation.Items.Select(item => new OrderEditItemRequest(item.ProductId, item.Quantity)),
                new OrderEditItemRequest(incomplete, 4m)
            ]
        };

        var response = await PutOrderAsync(client, tenantId, order, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.True(saved.Quotation.IsRetail);
        Assert.Contains(saved.Quotation.Items, item => item.ProductId == incomplete);
    }

    // Un producto cuyas escalas salen de copiar las de otro: la copia deja el tramo sin
    // restriccion, que es lo unico que hoy produce una escala incompleta.
    private static async Task<Guid> ProductWithIncompleteScalesAsync(HttpClient client, Guid tenantId)
    {
        var source = await CreateProductWithScalesAsync(client, tenantId);
        var target = await CreateProductWithScalesAsync(client, tenantId, scales: []);
        var copied = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/price-scales/copy",
            new { sourceProductId = source, targetProductIds = new[] { target } },
            TestContext.Current.CancellationToken);
        copied.EnsureSuccessStatusCode();
        return target;
    }

    private static async Task<QuotationResponse> DraftWithTenUnitsAsync(
        HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var created = await CreateQuotationAsync(client, tenantId, clientId);
        var added = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, created.Id)}/items",
            new AddQuotationItemRequest(productId, 10m),
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
    // GlobalScaleDiscountApiTests.RequestFor.
    private static SaveQuotationRequest RequestFor(
        QuotationResponse quotation, bool isRetail, int? globalScaleFloor) =>
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
            globalScaleFloor,
            isRetail);

    // Las lineas del pedido son la lista entera deseada: se mandan las que ya estan para que el
    // guardado no las borre. Una cantidad null las deja como estan.
    private static SaveOrderEditsRequest OrderRequestFor(
        QuotationResponse quotation, decimal? quantity, bool isRetail) =>
        new(
            quotation.Items
                .Select(item => new OrderEditItemRequest(item.ProductId, quantity ?? item.Quantity))
                .ToArray(),
            new OrderEditProofsRequest(null, null, null),
            Notes: null,
            quotation.GlobalScaleFloor,
            isRetail);

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

    private static async Task<QuotationHistoryResponse> GetHistoryAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationUrl(tenantId, quotationId)}/history",
            TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        return history;
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
