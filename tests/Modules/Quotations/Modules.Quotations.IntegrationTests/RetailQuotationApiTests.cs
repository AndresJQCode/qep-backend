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
/// Que el corte funcione en memoria ya lo cubre <c>QuotationPricingRecalculationTests</c>. Lo que
/// este archivo verifica es lo que esa no puede ver: que el flag se persista, que prenderlo
/// limpie el piso global que ya estuviera elegido, que el mismo endpoint sirva a la pantalla de
/// cotizacion y a la de pedido, y que el rastro quede en historial y auditoria.
/// </summary>
public sealed class RetailQuotationApiTests
{
    private static string RetailUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/retail";

    private static string GlobalScaleUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/global-scale";

    [Fact]
    public async Task TurningRetailOnLeavesEveryLineWithoutDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // 10 unidades caen en el tramo 10-19 de DefaultScales, que descuenta 5%, y 1.000.000 pasa
        // la compra minima de sobra: el descuento es real antes de prender el detal, asi que lo
        // que la prueba mide es el interruptor y no la compuerta.
        var added = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var before = await added.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.False(before.IsRetail);
        Assert.Equal(5m, Assert.Single(before.Items).DiscountPercentage);

        var response = await client.PutAsJsonAsync(
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.True(updated.IsRetail);

        var item = Assert.Single(updated.Items);
        Assert.Equal(0m, item.DiscountPercentage);
        Assert.Equal("Own", item.DiscountOrigin);

        // Sin descuento el total sube: 10 unidades a lista contra las mismas 10 con el 5%.
        Assert.True(updated.Total > before.Total);
        Assert.Equal(0m, updated.DiscountAmount);
    }

    // Detal y piso global son excluyentes. Prender el detal limpia el piso en vez de guardarlo
    // dormido: una respuesta que siguiera publicando el piso le haria pintar a la pantalla un
    // descuento de escala que el recalculo esta ignorando.
    [Fact]
    public async Task TurningRetailOnClearsTheGlobalScaleFloor()
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
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var applied = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);
        applied.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.True(updated.IsRetail);
        Assert.Null(updated.GlobalScaleFloor);
        Assert.Equal(0m, Assert.Single(updated.Items).DiscountPercentage);

        // Y no vuelve al apagar: el piso es una eleccion del asesor, no un estado derivado.
        var off = await client.PutAsJsonAsync(
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(false),
            TestContext.Current.CancellationToken);
        off.EnsureSuccessStatusCode();
        var afterOff = await off.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(afterOff);
        Assert.False(afterOff.IsRetail);
        Assert.Null(afterOff.GlobalScaleFloor);
    }

    // La otra mitad de la exclusion: con detal prendido el select de escala global no tiene nada
    // que ofrecer, asi que mandar un piso es un bug del cliente y se dice como tal.
    [Fact]
    public async Task WithRetailOnTheGlobalScaleEndpointRejectsAFloor()
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
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var retail = await client.PutAsJsonAsync(
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(true),
            TestContext.Current.CancellationToken);
        retail.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("quotation.retail.floor_not_allowed", problem.Code);

        // Quitarlo si: null no pide descuento, y rechazarlo obligaria a apagar el detal para
        // limpiar un piso que el propio detal ya dejo en null.
        var cleared = await client.PutAsJsonAsync(
            GlobalScaleUrl(tenantId, quotation.Id),
            new SetGlobalScaleRequest(null),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    }

    // Mismo endpoint para las dos pantallas, igual que el editor de lineas y el piso global: el
    // pedido pendiente corrige el detal sin que la cotizacion vuelva a ser editable.
    [Fact]
    public async Task ThePendingOrderCanStillChangeTheRetailFlag()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
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

        var response = await client.PutAsJsonAsync(
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.True(updated.IsRetail);

        // Y se persistio: la lectura posterior lo trae igual.
        var reloaded = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsRetail);
    }

    // El pedido que ya no esta Pending no admite cambiarlo: la rama de la doble puerta.
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
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("order.order.not_pending", problem.Code);
    }

    // Una cotizacion anulada no es editable y no tiene pedido que la rescate: la otra rama.
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
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();
        var voided = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);
        voided.EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(true),
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
            new AddQuotationItemRequest(productId, 10m),
            TestContext.Current.CancellationToken);
        added.EnsureSuccessStatusCode();

        var applied = await client.PutAsJsonAsync(
            RetailUrl(tenantId, quotation.Id),
            new SetRetailRequest(true),
            TestContext.Current.CancellationToken);
        applied.EnsureSuccessStatusCode();

        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/history",
            TestContext.Current.CancellationToken);
        Assert.NotNull(history);
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
                    == "quotation.quotation.retail_changed"
                && recorded.RootElement.GetProperty("resourceId").GetString()
                    == quotation.Id.ToString();
        });
    }

    private sealed record ProblemPayload(string Code);
}
