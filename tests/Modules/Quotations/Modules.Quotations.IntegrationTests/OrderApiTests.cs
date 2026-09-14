using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>US-13 a US-17: conversión de una cotización en pedido. Ya no hace falta haberla
/// enviado (a pedido, 2026-09).</summary>
public sealed class OrderApiTests
{
    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    private static string OrderProofsUrl(Guid tenantId, Guid quotationId) =>
        $"{OrderUrl(tenantId, quotationId)}/proofs";

    // Bug real, 2026-09-12: el editor de cotizaciones dejo de pedir la forma de pago hace
    // rato (ver `UpdateQuotationRequest` en el frontend), asi que toda cotizacion nueva la
    // tiene en null -- pero `EnsureConvertibleToOrder` seguia exigiendola, y con eso "Convertir
    // en pedido" no aparecia nunca para nadie. La cobertura vieja probaba lo contrario (que
    // rechazara sin forma de pago); esta prueba el arreglo.
    [Fact]
    public async Task ConvertWithoutAPaymentMethodSucceeds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenantId, clientId, productId, paymentMethod: null);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                "Pago verificado",
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ConvertCreatesTheOrderAndLeavesTheQuotationConverted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", "Pago verificado", [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        // Nace Pending desde aa020a8: convertir dejo de ser aprobar. Quien convierte y quien da
        // el visto bueno son roles distintos, y el estado es lo que hace visible ese paso.
        Assert.Equal("Pending", order.Status);
        Assert.Equal("FullPaymentReceived", order.PaymentStatus);
        Assert.Equal(quotation.Id, order.QuotationId);
        Assert.StartsWith(
            $"PED-{DateTime.UtcNow.Year}-", order.OrderNumber, StringComparison.Ordinal);
        Assert.Null(order.RitualCollectionSyncId);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(proofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);

        // Convertir deja la cotizacion en Converted, en la misma unidad de trabajo que crea el
        // pedido: de ahi en adelante es de solo lectura y ya no ofrece convertirse otra vez.
        var fetchedQuotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetchedQuotation);
        Assert.Equal("Converted", fetchedQuotation.Status);
        Assert.False(fetchedQuotation.CanBeConvertedToOrder);

        // Y el listado la filtra por ese estado (QuotationListing.ParseStatus, contra la base):
        // ya no aparece entre las enviadas.
        var converted = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?status=Converted", TestContext.Current.CancellationToken);
        var sent = await client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(tenantId)}?status=Sent", TestContext.Current.CancellationToken);
        Assert.NotNull(converted);
        Assert.NotNull(sent);
        Assert.Equal(quotation.Id, Assert.Single(converted.Items).Id);
        Assert.Equal(0, sent.Total);
    }

    // US-10/US-11: convertida, la cotizacion queda de solo lectura. Mientras se quedaba en Sent
    // despues de convertirse, todo esto seguia permitido: se podia editar, anular o reenviar una
    // cotizacion cuyo pedido ya existia.
    [Fact]
    public async Task AConvertedQuotationCanNoLongerBeEditedVoidedOrResent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
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
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(firstProofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var secondProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(secondProofFileId, quotation.Total)]),
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
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        await InsertLegacyOrderAsync(database.GetConnectionString(), tenantId, quotation.Id);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("quotation.quotation.already_converted", body, StringComparison.Ordinal);
    }

    /// <summary>Un pedido "legado" para una cotización que siguió en Sent. Número fuera de la
    /// secuencia a propósito: lo único que tiene que chocar es el índice de la cotización.</summary>
    private static async Task InsertLegacyOrderAsync(string connectionString, Guid tenantId, Guid quotationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO quotations.orders (
                id, tenant_id, order_number, quotation_id, status, payment_status, notes, converted_at,
                converted_by, approved_at, approved_by, ritual_collection_sync_id, created_at, updated_at, version)
            VALUES (
                @id, @tenantId, 'LEGADO-0001', @quotationId, 'Pending', 'PaymentPending', NULL, now(),
                @convertedBy, NULL, NULL, NULL, now(), now(), 1)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("quotationId", quotationId);
        command.Parameters.AddWithValue("convertedBy", Guid.CreateVersion7());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    // US-14: sin comprobantes se permite unicamente cuando el pago queda pendiente.
    [Fact]
    public async Task ConvertWithPaymentPendingRequiresNoProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Empty(order.PaymentProofs);
    }

    [Fact]
    public async Task ConvertWithoutProofsWhenPaymentIsNotPendingIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PartialPaymentReceived", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // US-13: ya no hace falta haberla enviado (a pedido, 2026-09), pero un borrador recien
    // creado por este helper no tiene productos ni cuenta de cobro -- se rechaza por eso, no
    // por seguir en Draft.
    [Fact]
    public async Task ConvertADraftQuotationIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ConvertWithAnInvalidPaymentStatusIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("NotAStatus", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ConvertWithAZeroAmountProofIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, 0m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task GetOrderReturnsTheConvertedOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var created = await (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var response = await client.GetAsync(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.OrderNumber, fetched.OrderNumber);
    }

    [Fact]
    public async Task GetOrderForAnUnconvertedQuotationReturnsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
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
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var productId = await CreateProductWithScalesAsync(owner, tenantId);
        var quotation = await CreateSentQuotationAsync(owner, factory, tenantId, clientId, productId);

        var (_, _, otherOwner) = await RegisterTenantAsync(factory, OrdersPermissions.OrderManage);
        using var __ = otherOwner;

        var response = await otherOwner.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // A pedido (2026-09), editar una enviada ya no la vuelve inconvertible: sigue siendo
    // editable (US-10) y el pedido hereda lo que la cotizacion tiene guardado ahora, sin exigir
    // reenviarla primero.
    [Fact]
    public async Task ConvertAQuotationEditedAfterBeingSentSucceeds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
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
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ResendingMakesAnEditedQuotationConvertibleAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
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
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // A pedido (2026-09): "Aprobar pedido" se bloquea mientras el pago no esta completo, y esta
    // ruta es la forma de destrabarlo sin recrear el pedido -- cargar lo que falto al convertir.
    [Fact]
    public async Task AddPaymentProofsAddsThemAndUpdatesThePaymentStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        convert.EnsureSuccessStatusCode();

        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [new OrderPaymentProofRequest(proofFileId, quotation.Total)],
                Notes: "Pago completado por transferencia"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("FullPaymentReceived", order.PaymentStatus);
        Assert.Equal("Pago completado por transferencia", order.Notes);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(proofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);
    }

    // A pedido (2026-09): corregir el monto de un comprobante ya cargado, en el mismo request
    // que sumaria uno nuevo -- un solo viaje de red para las dos cosas.
    [Fact]
    public async Task AddPaymentProofsCorrectsAnExistingProofAmount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "PartialPaymentReceived",
                null,
                [new OrderPaymentProofRequest(firstProofFileId, 10_000m)]),
            TestContext.Current.CancellationToken);
        convert.EnsureSuccessStatusCode();
        var created = await convert.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        var existingProofId = Assert.Single(created.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(existingProofId, quotation.Total)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal("FullPaymentReceived", order.PaymentStatus);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(existingProofId, proof.Id);
        Assert.Equal(firstProofFileId, proof.FileId);
        Assert.Equal(quotation.Total, proof.Amount);
    }

    // Corregir un comprobante que no existe en este pedido -- de otro pedido, o un id inventado --
    // no puede pasar como si fuera valido.
    [Fact]
    public async Task AddPaymentProofsRejectsCorrectingAProofThatDoesNotExistOnThisOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        convert.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "PaymentPending",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 1_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // Aprobado, el pedido es el respaldo de un cobro que alguien ya reviso con lo que habia en
    // ese momento: sumarle comprobantes ahi adentro cambiaria lo que esa persona dio por bueno.
    [Fact]
    public async Task AddPaymentProofsRejectsAnAlreadyApprovedOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var firstProofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var convert = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(firstProofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        convert.EnsureSuccessStatusCode();
        var approve = await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve",
            null,
            TestContext.Current.CancellationToken);
        approve.EnsureSuccessStatusCode();

        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(proofFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // Acciones de auditoría nuevas (spec 2026-09-14). Las filas viejas de audit.entries quedan como
    // están (D4); lo que se prueba es lo que se escribe desde ahora.
    [Fact]
    public async Task AddingProofsAndApprovingAreAuditedAsOrderActions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve",
            null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var actions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(ActionOf)
            .ToArray();

        Assert.Contains("quotation.order.payment_proofs_added", actions);
        Assert.Contains("quotation.order.approved", actions);
        Assert.DoesNotContain(actions, action => action.StartsWith("quotation.sale.", StringComparison.Ordinal));
    }

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    /// <summary>Una edicion cualquiera sobre la cotizacion ya enviada: mueve UpdatedAt por
    /// delante de SentAt, que es lo unico que define "cambio despues de enviarse".</summary>
    private static async Task ChangeTheQuantityAsync(
        HttpClient client, Guid tenantId, QuotationResponse quotation)
    {
        var itemId = Assert.Single(quotation.Items).Id;
        var response = await client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items/{itemId}",
            new UpdateQuotationItemRequest(2m),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
