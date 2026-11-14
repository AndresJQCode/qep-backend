using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Convertir una cotización en pedido: numeración, rechazos, lectura y permisos.</summary>
public sealed class OrderConversionRulesApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    /// <summary>
    /// El caso que motivó el spec 2026-09-17 de numeración: un tenant que viene de otro sistema
    /// sigue su propio consecutivo, con su prefijo y sin año. El contador es la fila `year = 0`, la
    /// que no se reinicia. Reloj fijo en la frontera de fin de año de Bogotá para que el año no
    /// dependa del calendario de la máquina — y para dejar a la vista que acá el año no se usa.
    /// </summary>
    [Fact]
    public async Task ConvertUsesThePrefixAndCounterConfiguredForTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, tenantId, "order", "PW", includeYear: false, "", 1);
        // El siguiente número que queremos que salga, no el último que emitió el sistema viejo.
        await SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 234_235L);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);

        var first = await ConvertOneAsync(client, factory, tenantId, clientId, productId);
        var second = await ConvertOneAsync(client, factory, tenantId, clientId, productId);

        Assert.Equal("PW234235", first);
        Assert.Equal("PW234236", second);
    }

    // US-10/US-11: convertida y con el pedido ya aprobado, la cotizacion queda de solo lectura
    // del todo. Mientras se quedaba en Sent despues de convertirse, todo esto seguia permitido:
    // se podia editar, anular o reenviar una cotizacion cuyo pedido ya existia. Se aprueba el
    // pedido a proposito (a pedido, 2026-09-15): mientras sigue Pending, la cantidad de una
    // linea SI se puede editar desde "Editar" pedido -- ver OrderItemEditApiTests -- asi que
    // esta prueba usa el estado en el que ni siquiera eso queda permitido.
    [Fact]
    public async Task AConvertedQuotationWithAnApprovedOrderCanNoLongerBeEditedVoidedOrResent()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var itemId = Assert.Single(quotation.Items).Id;
        var edited = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(2m),
            TestContext.Current.CancellationToken);
        var voided = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/void",
            content: null,
            TestContext.Current.CancellationToken);
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var resent = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);

        // Spec 2026-10-15 §4.5: la cotización convertida está congelada, con o sin pedido aprobado.
        await AssertUnprocessableAsync(edited, "quotation.quotation.not_editable");
        await AssertUnprocessableAsync(voided, "quotation.quotation.not_editable");
        await AssertUnprocessableAsync(resent, "quotation.quotation.not_draft");

        static async Task AssertUnprocessableAsync(HttpResponseMessage response, string code)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(code, body, StringComparison.Ordinal);
        }
    }

    // Una segunda conversion la corta el estado: la cotizacion ya quedo en Converted, y
    // EnsureConvertibleToOrder solo deja pasar Draft y Sent, asi que el 422 es
    // status_not_convertible. El indice unico de Order.QuotationId (que QuotationsUnitOfWork
    // traduce a already_converted) y el token de concurrencia de la cotizacion quedan como red de
    // abajo para dos conversiones simultaneas.
    [Fact]
    public async Task ConvertingAnAlreadyConvertedQuotationIsRejected()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(firstProofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var secondProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(secondProofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("quotation.quotation.status_not_convertible", body, StringComparison.Ordinal);
    }

    // La red de abajo del índice único de Order.QuotationId (QuotationsUnitOfWork): un pedido de
    // antes de que existiera Converted dejó su cotización en Sent, así que el estado no corta la
    // segunda conversión y la corta el índice. Si el nombre del índice cambia y la constante no, esto
    // sale 500 con el nombre de la constraint adentro (spec 2026-09-14, «Riesgos»).
    [Fact]
    public async Task ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted()
    {
        var database = sharedHost.Database;
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await InsertLegacyOrderAsync(database.GetConnectionString(), tenantId, quotation.Id);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("quotation.quotation.already_converted", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// El formato de numeración es dato de un tenant desde el spec 2026-09-17: un prefijo mal
    /// escrito a mano puede alcanzar el mismo texto que un pedido ya emitido. Mismo patrón que
    /// <see cref="ConvertingAQuotationThatAlreadyHasAnOrderIsAlreadyConverted"/> con la otra
    /// constraint -- acá la que protege el número, no la que protege la conversión -- para que la
    /// colisión salga 422 <c>order.order.number_taken</c> y no 500 con el nombre del índice adentro.
    /// </summary>
    [Fact]
    public async Task AMisconfiguredPrefixThatCollidesWithAnAlreadyIssuedOrderNumberIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);

        // Con el formato por defecto (sin fila) el primer pedido sale "PED-2026-0001".
        var firstNumber = await ConvertOneAsync(client, factory, tenantId, clientId, productId);
        Assert.Equal("PED-2026-0001", firstNumber);

        // Un prefijo que ya trae el año, sin año propio en el formato, arma el mismo texto que el
        // pedido anterior -- IX_orders_tenant_number, no IX_orders_quotation.
        await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
            factory, tenantId, "order", "PED-2026-", includeYear: false, "", 4);
        await SetOrderCounterAsync(factory, tenantId, year: 0, nextValue: 1L);

        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "Pago verificado", [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body2 = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.number_taken", body2, StringComparison.Ordinal);
    }

    // Regla de negocio: los números de pedido no tienen huecos. Una conversión que el dominio
    // rechaza no puede gastar un número: la precondición se revisa antes de pedirlo.
    [Fact]
    public async Task AConversionRejectedByTheDomainDoesNotConsumeAnOrderNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var rejected = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        var next = await ConvertOneAsync(client, factory, tenantId, clientId, productId);
        Assert.Equal("PED-2026-0002", next);
    }

    // La falla llega al guardar, con el número ya tomado: el índice de Order.QuotationId choca con
    // un pedido legado. El incremento del contador tiene que deshacerse con el pedido que no se
    // guardó, o el siguiente pedido deja un hueco.
    [Fact]
    public async Task AConversionThatFailsWhenSavingDoesNotConsumeAnOrderNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await InsertLegacyOrderAsync(database.GetConnectionString(), tenantId, quotation.Id);

        var rejected = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        var next = await ConvertOneAsync(client, factory, tenantId, clientId, productId);
        Assert.Equal("PED-2026-0001", next);
    }

    // Varios asesores convierten la misma cotización a la vez: gana uno y los demás salen 409
    // (token de concurrencia) o 422 (estado o IX_orders_quotation). Ninguno de los perdedores
    // puede dejar un número gastado, así que el pedido siguiente es el consecutivo del ganador.
    [Fact]
    public async Task ConcurrentConversionsOfTheSameQuotationLeaveNoGapInOrderNumbers()
    {
        const int Attempts = 5;
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var responses = await Task.WhenAll(Enumerable.Range(0, Attempts).Select(_ =>
            client.PostAsJsonAsync(
                OrderUrl(tenantId, quotation.Id),
                new ConvertQuotationToOrderRequest(null, []),
                TestContext.Current.CancellationToken)));

        var created = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(
            responses.Where(response => response != created),
            response => Assert.Contains(
                response.StatusCode,
                new[] { HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity }));
        var winner = await created.Content.ReadConvertedOrderAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(winner);
        Assert.Equal("PED-2026-0001", winner.OrderNumber);

        var next = await ConvertOneAsync(client, factory, tenantId, clientId, productId);
        Assert.Equal("PED-2026-0002", next);
    }

    [Fact]
    public async Task GetOrderReturnsTheConvertedOrder()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var created = await (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken))
            .Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var response = await client.GetAsync(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Spec 2026-10-15 §6.2: por la cotización también responde el detalle compuesto del pedido.
        var fetched = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Order.Id);
        Assert.Equal(created.OrderNumber, fetched.Order.OrderNumber);
        Assert.Equal(quotation.Id, fetched.SourceQuotation?.Id);
    }

    [Fact]
    public async Task GetOrderForAnUnconvertedQuotationReturnsNotFound()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.GetAsync(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConvertForAnotherTenantIsForbidden()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var productId = await CreateProductWithScalesAsync(owner, tenantId);
        var quotation = await CreateSentQuotationAsync(owner, factory, tenantId, clientId, productId);

        var (_, _, otherOwner) = await RegisterTenantAsync(factory, OrdersPermissions.OrderManage);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // A pedido (2026-09), editar una enviada ya no la vuelve inconvertible: sigue siendo
    // editable (US-10) y el pedido hereda lo que la cotizacion tiene guardado ahora, sin exigir
    // reenviarla primero.
    [Fact]
    public async Task ConvertAQuotationEditedAfterBeingSentSucceeds()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ChangeTheQuantityAsync(client, tenantId, quotation);

        // Y la cotizacion lo dice al leerla: HasChangesSinceSent sigue en true -- se editó de
        // verdad --, pero ya no es uno de los motivos de CanBeConvertedToOrder.
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.True(fetched.HasChangesSinceSent);
        Assert.True(fetched.CanBeConvertedToOrder);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ResendingMakesAnEditedQuotationConvertibleAgain()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await ChangeTheQuantityAsync(client, tenantId, quotation);

        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        var resent = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        resent.EnsureSuccessStatusCode();
        var afterResend = await resent.Content.ReadFromJsonAsync<QuotationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(afterResend);
        Assert.False(afterResend.HasChangesSinceSent);
        Assert.True(afterResend.CanBeConvertedToOrder);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
