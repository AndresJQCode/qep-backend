using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-09-17: editar un pedido como borrador. El guardado atómico
/// (<c>PUT /quotations/{id}/order</c> con <c>If-Match</c>) y el cálculo previo
/// (<c>POST /quotations/{id}/order/preview</c>), contra Postgres real.
/// </summary>
public sealed class OrderEditsApiTests
{
    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    // Decisión 4: el cálculo previo muta el agregado en memoria, así que la lectura no puede dejar
    // nada en el change tracker que un SaveChangesAsync del mismo scope llegue a persistir.
    [Fact]
    public async Task FindUntrackedLoadsBothAggregatesWithoutTrackingThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var quotations = scope.ServiceProvider.GetRequiredService<IQuotationRepository>();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var quotationId = new QuotationId(quotation.Id);

        var loadedQuotation = await quotations.FindUntrackedAsync(
            tenantId, quotationId, TestContext.Current.CancellationToken);
        var loadedOrder = await orders.FindUntrackedAsync(
            tenantId, quotationId, TestContext.Current.CancellationToken);

        Assert.NotNull(loadedQuotation);
        Assert.NotNull(loadedOrder);
        Assert.Single(loadedQuotation.Items);
        Assert.Single(loadedOrder.PaymentProofs);
        Assert.Empty(dbContext.ChangeTracker.Entries());

        loadedOrder.UpdateNotes("Borrador que no se guarda", DateTimeOffset.UtcNow);
        Assert.Equal(0, await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));

        // El filtro de tenant es parte de la consulta, como en FindAsync.
        Assert.Null(await quotations.FindUntrackedAsync(
            Guid.CreateVersion7(), quotationId, TestContext.Current.CancellationToken));
        Assert.Null(await orders.FindUntrackedAsync(
            Guid.CreateVersion7(), quotationId, TestContext.Current.CancellationToken));
    }

    // Decisión 6: la versión viaja al frontend para mandarla en If-Match. Aprobar sube la versión
    // exactamente una vez (Order.Approve), así que la segunda lectura es predecible.
    [Fact]
    public async Task TheOrderResponseCarriesTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);
        Assert.True(order.Version >= 1);

        var approve = await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var body = await approve.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            Assert.Equal(order.Version + 1, json.RootElement.GetProperty("version").GetInt64());
        }

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(order.Version + 1, detail.Order.Version);
    }

    // Spec 2026-09-17, pruebas: productos + comprobantes + notas en una transacción, versión nueva.
    // Reemplazo total del producto a propósito: bajar antes de subir dispararía last_item_required.
    [Fact]
    public async Task SaveAppliesProductsProofsAndNotesTogetherAndReturnsTheNewVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);
        var replacementId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(replacementId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, 20_000m)], null, null),
                "Entregar el lunes"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(replacementId, Assert.Single(saved.Quotation.Items).ProductId);
        Assert.Equal(50_000m, saved.Quotation.Total);
        Assert.Equal("Entregar el lunes", saved.Order.Notes);
        Assert.Equal(20_000m, Assert.Single(saved.Order.PaymentProofs).Amount);
        Assert.Equal("PartialPaymentReceived", saved.Order.PaymentStatus);
        Assert.True(saved.Order.Version > order.Version);
        Assert.Equal($"\"{saved.Order.Version}\"", response.Headers.ETag?.Tag);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(replacementId, Assert.Single(fetched.Quotation.Items).ProductId);
        Assert.Equal(saved.Order.Version, fetched.Order.Version);
        Assert.Equal("PartialPaymentReceived", fetched.Order.PaymentStatus);

        var auditActions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(ActionOf)
            .ToArray();
        Assert.Contains("quotation.order.item_added", auditActions);
        Assert.Contains("quotation.order.item_removed", auditActions);
        Assert.Contains("quotation.order.payment_proofs_added", auditActions);
    }

    // Decisión 5: el cliente ya no manda paymentStatus; comprobantes que cubren el total lo dejan
    // FullPaymentReceived.
    [Fact]
    public async Task SaveDerivesThePaymentStatusOnTheServer()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, quotation.Total)], null, null),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("FullPaymentReceived", (await ReadDetailAsync(response)).Order.PaymentStatus);
    }

    // Paso 5 del spec: quitar y corregir en el mismo guardado, con el evento detached en el outbox.
    [Fact]
    public async Task SaveRemovesAndCorrectsProofsAndRecalculatesThePaymentStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var keptFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var removedFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var converted = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null,
                [new OrderPaymentProofRequest(keptFileId, 60_000m), new OrderPaymentProofRequest(removedFileId, 40_000m)]),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        var kept = Assert.Single(order.PaymentProofs, proof => proof.FileId == keptFileId);
        var removed = Assert.Single(order.PaymentProofs, proof => proof.FileId == removedFileId);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest(null, [new OrderPaymentProofUpdateRequest(kept.Id, 50_000m)], [removed.Id]),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        var remaining = Assert.Single(saved.Order.PaymentProofs);
        Assert.Equal(kept.Id, remaining.Id);
        Assert.Equal(50_000m, remaining.Amount);
        Assert.Equal("PartialPaymentReceived", saved.Order.PaymentStatus);
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.payment-proofs-detached.v1"));
    }

    // Atomicidad: el proofId ajeno falla después de aplicar los productos en memoria, y nada
    // persiste. El fileId inexistente falla antes de mutar, con su propio código.
    [Fact]
    public async Task AFailureHalfwayPersistsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var unknownProof = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 2m)],
                new OrderEditProofsRequest(null, null, [Guid.CreateVersion7()]),
                "No se guarda"));
        var unknownFile = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 2m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(Guid.CreateVersion7(), 10_000m)], null, null),
                "No se guarda"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownProof.StatusCode);
        Assert.Equal("order.payment_proof.not_found", (await ReadProblemAsync(unknownProof)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownFile.StatusCode);
        Assert.Equal("order.payment_proof.file_not_found", (await ReadProblemAsync(unknownFile)).Code);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(1m, Assert.Single(fetched.Quotation.Items).Quantity);
        Assert.Null(fetched.Order.Notes);
        Assert.Empty(fetched.Order.PaymentProofs);
        Assert.Equal(order.Version, fetched.Order.Version);
    }

    // Decisión 6: If-Match viejo → 412; sin If-Match → 428, mismo contrato que /roles.
    [Fact]
    public async Task AStaleOrMissingIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var body = new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, "Primera");
        (await PutEditsAsync(client, tenantId, quotation.Id, order.Version, body)).EnsureSuccessStatusCode();

        var stale = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version, body with { Notes = "Segunda" });
        var missing = await PutEditsAsync(client, tenantId, quotation.Id, null, body with { Notes = "Tercera" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ReadProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ReadProblemAsync(missing)).Code);
        Assert.Equal("Primera", (await GetDetailAsync(client, tenantId, order.Id)).Order.Notes);
    }

    [Fact]
    public async Task AnApprovedOrderIsNotEditable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, _, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var approve = await client.PostAsync(
            $"{OrderUrl(tenantId, quotation.Id)}/approve", null, TestContext.Current.CancellationToken);
        approve.EnsureSuccessStatusCode();
        var approved = await approve.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(approved);

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, approved.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 2m)], null, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_pending", (await ReadProblemAsync(response)).Code);
    }

    // «Sin ningún cambio real: responde 200 con el estado actual, sin historial ni auditoría y sin
    // subir Version.»
    [Fact]
    public async Task SavingWithoutChangesKeepsTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var auditBefore = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count;

        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(order.Version, (await ReadDetailAsync(response)).Order.Version);
        Assert.Equal(order.Version, (await GetDetailAsync(client, tenantId, order.Id)).Order.Version);
        Assert.Equal(auditBefore, (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count);
    }

    // Carry-over de la revisión de la tarea 5: un cuerpo inválido tiene que llegar a 422 por HTTP
    // de verdad, así que el validador concreto (SaveOrderEditsValidator) queda registrado por el
    // escaneo de ensamblado y no sólo compilando en la solución.
    [Fact]
    public async Task AnInvalidBodyIsRejectedByTheRegisteredValidator()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);

        // Items vacío: "At least one product is required." (OrderEditsValidator<T>).
        var response = await PutEditsAsync(
            client, tenantId, quotation.Id, order.Version,
            new SaveOrderEditsRequest([], null, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", (await ReadProblemAsync(response)).Code);
    }

    /// <summary>Una cotización enviada con un producto de 100.000 COP sin impuesto, convertida con el
    /// pago pendiente y sin comprobantes: el total es 100.000 y el estado de pago lo decide cada
    /// prueba.</summary>
    private static async Task<(QuotationResponse Quotation, OrderResponse Order, Guid ProductId)> CreatePendingOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return (quotation, order, productId);
    }

    private static async Task<HttpResponseMessage> PutEditsAsync(
        HttpClient client, Guid tenantId, Guid quotationId, long? version, SaveOrderEditsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, OrderUrl(tenantId, quotationId))
        {
            Content = JsonContent.Create(body),
        };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response)
    {
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<OrderDetailResponse> GetDetailAsync(HttpClient client, Guid tenantId, Guid orderId)
    {
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{orderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    // Misma lectura que OrderApiTests.ActionOf (OrderApiTests.cs:1163-1167): el payload del evento de
    // auditoría lleva la acción en `action`.
    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
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
