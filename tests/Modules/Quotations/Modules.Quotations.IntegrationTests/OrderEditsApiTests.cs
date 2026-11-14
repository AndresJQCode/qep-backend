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
/// (<c>PUT /orders/{orderId}</c> con <c>If-Match</c>) y el cálculo previo
/// (<c>POST /orders/{orderId}/preview</c>), contra Postgres real.
/// </summary>
public sealed class OrderEditsApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    // La edición se direcciona por el id del pedido y no por el de su cotización: quien llega
    // desde el listado tiene el pedido, no la cotización.
    private static string OrderByIdUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    // Spec 2026-10-15 §4.5: editar el pedido pendiente edita el pedido; la cotización de origen
    // queda congelada como el documento que el cliente vio.
    [Fact]
    public async Task SavingEditsChangesTheOrderAndLeavesTheQuotationUntouched()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 7m)], null, "Nota del pedido", null, false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(7m, Assert.Single(detail.Items).Quantity);
        Assert.Equal("Nota del pedido", detail.Order.Notes);
        Assert.Contains(detail.History, entry => entry.EventType == "ItemsChanged");
        Assert.Equal((quotation.Id, quotation.QuotationNumber), (detail.SourceQuotation!.Id, detail.SourceQuotation.QuotationNumber));

        var frozen = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(1m, Assert.Single(frozen!.Items).Quantity);
        Assert.Equal("Converted", frozen.Status);
    }

    // Decisión 4: el cálculo previo muta el agregado en memoria, así que la lectura no puede dejar
    // nada en el change tracker que un SaveChangesAsync del mismo scope llegue a persistir.
    [Fact]
    public async Task FindUntrackedLoadsBothAggregatesWithoutTrackingThem()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var converted = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);

        await using var scope = factory.Services.CreateAsyncScope();
        var quotations = scope.ServiceProvider.GetRequiredService<IQuotationRepository>();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var quotationId = new QuotationId(quotation.Id);
        var orderId = new OrderId(order.Id);

        var loadedQuotation = await quotations.FindUntrackedAsync(
            tenantId, quotationId, TestContext.Current.CancellationToken);
        var loadedOrder = await orders.FindUntrackedByIdAsync(
            tenantId, orderId, TestContext.Current.CancellationToken);

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
        Assert.Null(await orders.FindUntrackedByIdAsync(
            Guid.CreateVersion7(), orderId, TestContext.Current.CancellationToken));
    }

    // Decisión 6: la versión viaja al frontend para mandarla en If-Match. Marcar pagado sube la
    // versión exactamente una vez (Order.MarkPaid), así que la segunda lectura es predecible.
    [Fact]
    public async Task TheOrderResponseCarriesTheVersion()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);
        Assert.True(order.Version >= 1);

        var approve = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/mark-paid", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var body = await approve.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            Assert.Equal(order.Version + 1, json.RootElement.GetProperty("version").GetInt64());
        }

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, order.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(order.Version + 1, detail.Order.Version);
    }

    // Spec 2026-09-17, pruebas: productos + comprobantes + notas en una transacción, versión nueva.
    // Reemplazo total del producto a propósito: bajar antes de subir dispararía last_item_required.
    [Fact]
    public async Task SaveAppliesProductsProofsAndNotesTogetherAndReturnsTheNewVersion()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);
        var replacementId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 50_000m);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(replacementId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, 20_000m)], null, null),
                "Entregar el lunes"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(replacementId, Assert.Single(saved.Items).ProductId);
        Assert.Equal(50_000m, saved.Order.Total);
        Assert.Equal("Entregar el lunes", saved.Order.Notes);
        Assert.Equal(20_000m, Assert.Single(saved.Order.PaymentProofs).Amount);
        Assert.Equal("PartialPaymentReceived", saved.Order.PaymentStatus);
        Assert.True(saved.Order.Version > order.Version);
        Assert.Equal($"\"{saved.Order.Version}\"", response.Headers.ETag?.Tag);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(replacementId, Assert.Single(fetched.Items).ProductId);
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
    // PendingVerification (spec 2026-11-10 §3.2: pagado sólo lo marca una persona).
    [Fact]
    public async Task SaveDerivesThePaymentStatusOnTheServer()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, quotation.Total)], null, null),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PendingVerification", (await ReadDetailAsync(response)).Order.PaymentStatus);
    }

    // Decisión del dueño de producto (2026-10-05): con retención en la fuente el pedido queda
    // por verificar cuando los comprobantes cubren el neto (NetTotal), no lo facturado (Total) — el
    // cliente no paga en efectivo lo que retiene. Un peso menos que el neto sigue siendo parcial.
    [Theory]
    [InlineData(0, "PendingVerification")]
    [InlineData(1, "PartialPaymentReceived")]
    public async Task SaveComparesTheProofsAgainstTheNetOfRetention(int shortBy, string expected)
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(
            client, factory, tenantId, withRetention: true);
        Assert.True(quotation.NetTotal < quotation.Total);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest(
                    [new OrderEditProofAddRequest(proofFileId, quotation.NetTotal - shortBy)], null, null),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (await ReadDetailAsync(response)).Order.PaymentStatus);
        Assert.Equal(expected, (await GetDetailAsync(client, tenantId, order.Id)).Order.PaymentStatus);
    }

    // Mismo criterio en el cálculo previo: el preview no puede mostrar un estado de pago distinto
    // del que va a dejar el guardado.
    [Fact]
    public async Task PreviewComparesTheProofsAgainstTheNetOfRetention()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(
            client, factory, tenantId, withRetention: true);
        Assert.True(quotation.NetTotal < quotation.Total);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview",
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest(
                    [new OrderEditProofAddRequest(proofFileId, quotation.NetTotal)], null, null),
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("PendingVerification", (await ReadDetailAsync(preview)).Order.PaymentStatus);
    }

    // Paso 5 del spec: quitar y corregir en el mismo guardado, con el evento detached en el outbox.
    [Fact]
    public async Task SaveRemovesAndCorrectsProofsAndRecalculatesThePaymentStatus()
    {
        var factory = await sharedHost.StartTestAsync();
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
                null,
                [new OrderPaymentProofRequest(keptFileId, 60_000m), new OrderPaymentProofRequest(removedFileId, 40_000m)]),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        var kept = Assert.Single(order.PaymentProofs, proof => proof.FileId == keptFileId);
        var removed = Assert.Single(order.PaymentProofs, proof => proof.FileId == removedFileId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
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

    // A pedido (2026-10-08): «Editar pedido» corrige la fecha del soporte de un comprobante que ya
    // existe en `proofs.update[].paidOn`, igual que el monto. Antes el campo no viajaba en la
    // corrección y la pantalla lo mandaba sin efecto.
    [Fact]
    public async Task SaveCorrectsThePaidOnDateOfAnExistingProof()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var converted = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                null,
                [new OrderPaymentProofRequest(proofFileId, quotation.Total, new DateOnly(2026, 10, 1))]),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        var existing = Assert.Single(order.PaymentProofs);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest(
                    null,
                    [new OrderPaymentProofUpdateRequest(
                        existing.Id, quotation.Total, PaidOn: new DateOnly(2026, 10, 7))],
                    null),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        var corrected = Assert.Single(saved.Order.PaymentProofs);
        Assert.Equal(existing.Id, corrected.Id);
        Assert.Equal(new DateOnly(2026, 10, 7), corrected.PaidOn);
    }

    // Spec 2026-11-10 §3.2: corregir el monto de un comprobante de un pedido pagado exige
    // quotations.order.approve, igual que quitarlo. Con sólo manage, bajar el monto a 1 y subir el
    // neto en el mismo guardado bajaría el pagado a parcial y borraría la marca de quien lo revisó.
    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden, "FullPaymentReceived")]
    [InlineData(true, HttpStatusCode.OK, "PartialPaymentReceived")]
    public async Task CorrectingAProofAmountOnAPaidOrderRequiresTheApprovePermission(
        bool withApprove, HttpStatusCode expectedStatus, string expectedPayment)
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, manager) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = manager;
        var (order, productId, proofId) = await CreatePaidOrderWithProofAsync(
            manager, factory, tenantId, ownerUserId);
        using var editor = withApprove
            ? CreateClient(factory, ownerUserId.ToString(), tenantId.ToString(), ApproverPermissions)
            : CreateClient(factory, ownerUserId.ToString(), tenantId.ToString(), ManagerPermissions);

        var response = await PutEditsAsync(
            editor, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 2m)],
                new OrderEditProofsRequest(null, [new OrderPaymentProofUpdateRequest(proofId, 1m)], null),
                null));

        Assert.Equal(expectedStatus, response.StatusCode);
        var fetched = await GetDetailAsync(manager, tenantId, order.Id);
        Assert.Equal(expectedPayment, fetched.Order.PaymentStatus);
        Assert.Equal(withApprove, fetched.Order.PaidMarkedAt is null);
    }

    // La misma regla por POST /proofs (`updatedProofs`): ningún camino con sólo manage corrige el
    // monto de un comprobante de un pedido pagado.
    [Fact]
    public async Task CorrectingAProofAmountOnAPaidOrderThroughTheProofsRouteIsForbiddenWithOnlyManage()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, manager) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = manager;
        var (order, _, proofId) = await CreatePaidOrderWithProofAsync(manager, factory, tenantId, ownerUserId);

        var response = await manager.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/proofs",
            new AddOrderPaymentProofsRequest([], UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 1m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var fetched = await GetDetailAsync(manager, tenantId, order.Id);
        Assert.Equal("FullPaymentReceived", fetched.Order.PaymentStatus);
        Assert.NotEqual(1m, Assert.Single(fetched.Order.PaymentProofs).Amount);
    }

    // Corregir sólo la fecha (el mismo monto) de un pedido pagado no toca el pago y sigue con manage.
    [Fact]
    public async Task CorrectingOnlyThePaidOnDateOfAPaidOrderProofStaysWithManage()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, manager) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = manager;
        var (order, productId, proofId) = await CreatePaidOrderWithProofAsync(
            manager, factory, tenantId, ownerUserId);
        var amount = Assert.Single(order.PaymentProofs).Amount;

        var response = await PutEditsAsync(
            manager, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest(
                    null, [new OrderPaymentProofUpdateRequest(proofId, amount, PaidOn: new DateOnly(2026, 10, 7))], null),
                null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("FullPaymentReceived", (await ReadDetailAsync(response)).Order.PaymentStatus);
    }

    // Atomicidad: el proofId ajeno falla después de aplicar los productos en memoria, y nada
    // persiste. El fileId inexistente falla antes de mutar, con su propio código.
    [Fact]
    public async Task AFailureHalfwayPersistsNothing()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var unknownProof = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 2m)],
                new OrderEditProofsRequest(null, null, [Guid.CreateVersion7()]),
                "No se guarda"));
        var unknownFile = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 2m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(Guid.CreateVersion7(), 10_000m)], null, null),
                "No se guarda"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownProof.StatusCode);
        Assert.Equal("order.payment_proof.not_found", (await ReadProblemAsync(unknownProof)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownFile.StatusCode);
        Assert.Equal("order.payment_proof.file_not_found", (await ReadProblemAsync(unknownFile)).Code);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(1m, Assert.Single(fetched.Items).Quantity);
        Assert.Null(fetched.Order.Notes);
        Assert.Empty(fetched.Order.PaymentProofs);
        Assert.Equal(order.Version, fetched.Order.Version);
    }

    // Decisión 6: If-Match viejo → 412; sin If-Match → 428, mismo contrato que /roles.
    [Fact]
    public async Task AStaleOrMissingIfMatchIsRejected()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var body = new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, "Primera");
        (await PutEditsAsync(client, tenantId, order.Id, order.Version, body)).EnsureSuccessStatusCode();

        var stale = await PutEditsAsync(
            client, tenantId, order.Id, order.Version, body with { Notes = "Segunda" });
        var missing = await PutEditsAsync(client, tenantId, order.Id, null, body with { Notes = "Tercera" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ReadProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ReadProblemAsync(missing)).Code);
        Assert.Equal("Primera", (await GetDetailAsync(client, tenantId, order.Id)).Order.Notes);
    }

    // Spec 2026-11-10 §3.3: preparado congela el pedido.
    [Fact]
    public async Task APreparedOrderIsNotEditable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var prepare = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/prepare", null, TestContext.Current.CancellationToken);
        prepare.EnsureSuccessStatusCode();
        var prepared = await prepare.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(prepared);

        var response = await PutEditsAsync(
            client, tenantId, prepared.Id, prepared.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 2m)], null, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_editable", (await ReadProblemAsync(response)).Code);
    }

    // «Sin ningún cambio real: responde 200 con el estado actual, sin historial ni auditoría y sin
    // subir Version.»
    [Fact]
    public async Task SavingWithoutChangesKeepsTheVersion()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var auditBefore = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count;

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
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
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);

        // Items vacío: "At least one product is required." (OrderEditsValidator<T>).
        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([], null, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", (await ReadProblemAsync(response)).Code);
    }

    // La ruta entra por el id del pedido: uno que no existe responde igual que el GET por id, con el
    // mismo código, tanto al guardar como al previsualizar.
    [Fact]
    public async Task AnUnknownOrderIdIsNotFound()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, _, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var unknownOrderId = Guid.CreateVersion7();
        var body = new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null);

        var save = await PutEditsAsync(client, tenantId, unknownOrderId, 1, body);
        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, unknownOrderId)}/preview", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
        Assert.Equal("order.order.not_found", (await ReadProblemAsync(save)).Code);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal("order.order.not_found", (await ReadProblemAsync(preview)).Code);
    }

    // Spec 2026-09-17, pruebas: el preview devuelve totales recalculados y un GET posterior muestra el
    // pedido sin cambios. Los montos de los comprobantes nuevos entran en el estado de pago (desvío 3).
    [Fact]
    public async Task PreviewReturnsTheRecalculatedDocumentWithoutPersistingIt()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var auditBefore = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count;

        var response = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview",
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 3m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(null, 300_000m)], null, null),
                "Borrador"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadDetailAsync(response);
        Assert.Equal(3m, Assert.Single(preview.Items).Quantity);
        Assert.Equal(300_000m, preview.Order.Total);
        Assert.Equal("PendingVerification", preview.Order.PaymentStatus);
        Assert.Equal("Borrador", preview.Order.Notes);
        Assert.Single(preview.Order.PaymentProofs);
        Assert.Equal(order.Version, preview.Order.Version);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(1m, Assert.Single(fetched.Items).Quantity);
        Assert.Equal(quotation.Total, fetched.Order.Total);
        Assert.Equal("PaymentPending", fetched.Order.PaymentStatus);
        Assert.Null(fetched.Order.Notes);
        Assert.Empty(fetched.Order.PaymentProofs);
        Assert.Equal(order.Version, fetched.Order.Version);
        Assert.Equal(auditBefore, (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Count);
    }

    // Final review del slice: el preview no tenía cobertura HTTP de proofs.update y
    // proofs.removeIds juntos. Mismo escenario que
    // SaveRemovesAndCorrectsProofsAndRecalculatesThePaymentStatus, pero por
    // POST /orders/{orderId}/preview — la proyección refleja el cambio y el GET posterior no ve nada
    // persistido (ni el comprobante quitado vuelve, ni el corregido queda con el monto nuevo).
    [Fact]
    public async Task PreviewAppliesProofUpdatesAndRemovalsWithoutPersistingThem()
    {
        var factory = await sharedHost.StartTestAsync();
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
                null,
                [new OrderPaymentProofRequest(keptFileId, 60_000m), new OrderPaymentProofRequest(removedFileId, 40_000m)]),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        var kept = Assert.Single(order.PaymentProofs, proof => proof.FileId == keptFileId);
        var removed = Assert.Single(order.PaymentProofs, proof => proof.FileId == removedFileId);

        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview",
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest(null, [new OrderPaymentProofUpdateRequest(kept.Id, 50_000m)], [removed.Id]),
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewed = await ReadDetailAsync(preview);
        var remaining = Assert.Single(previewed.Order.PaymentProofs);
        Assert.Equal(kept.Id, remaining.Id);
        Assert.Equal(50_000m, remaining.Amount);
        Assert.Equal("PartialPaymentReceived", previewed.Order.PaymentStatus);
        Assert.Equal(order.Version, previewed.Order.Version);

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(2, fetched.Order.PaymentProofs.Count);
        Assert.Contains(fetched.Order.PaymentProofs, proof => proof.Id == kept.Id && proof.Amount == 60_000m);
        Assert.Contains(fetched.Order.PaymentProofs, proof => proof.Id == removed.Id && proof.Amount == 40_000m);
        Assert.Equal("PendingVerification", fetched.Order.PaymentStatus);
        Assert.Equal(order.Version, fetched.Order.Version);
    }

    // «Errores de dominio (…) se devuelven como 422 con el mismo código que devolvería el guardado.»
    [Fact]
    public async Task PreviewReportsTheSameDomainErrorAsSaving()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var body = new SaveOrderEditsRequest(
            [new OrderEditItemRequest(productId, 1m), new OrderEditItemRequest(Guid.CreateVersion7(), 1m)],
            null,
            null);

        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview", body, TestContext.Current.CancellationToken);
        var save = await PutEditsAsync(client, tenantId, order.Id, order.Version, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, save.StatusCode);
        Assert.Equal("quotation.item.product_not_found", (await ReadProblemAsync(preview)).Code);
        Assert.Equal("quotation.item.product_not_found", (await ReadProblemAsync(save)).Code);
    }

    [Fact]
    public async Task PreviewOnAPreparedOrderIsNotEditable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/prepare", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview",
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 2m)], null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_editable", (await ReadProblemAsync(response)).Code);
    }

    // Spec 2026-10-26, «Editar pedido» cambia la facturación. El tenant de prueba es colombiano, así que la
    // regla NIT de la jurídica aplica.
    private static async Task<OrderBillingEditRequest> BillingEditAsync(
        HttpClient client,
        string name = "Otra S.A.S.",
        string personType = "Juridica",
        string identificationType = "NIT")
    {
        var home = (await EnsureCityIdsInTwoDepartmentsAsync(client)).First;
        return new OrderBillingEditRequest(
            new QuotationPartyRequest(
                name, "900123456", "3001234567", "otra@correo.co", "Calle 1 # 2-3", home.DepartmentId, home.CityId,
                PersonType: personType, IdentificationType: identificationType),
            // Lo manda el frontend viejo y se ignora (spec impuestos por tipo §9.3): las retenciones son
            // SaveOrderEditsRequest.WithholdingTaxRateIds.
            BillingWithRetention: true,
            BillingVatSurplus: false);
    }

    [Fact]
    public async Task EditingTheBillingPartySavesItWithPersonTypeAndRecalculates()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        Assert.Equal(0m, order.RetentionAmount);
        // Con la parte propia llegan sus selectores (spec impuestos por tipo D21): la retención es una tasa elegida.
        var retefuente = await InsertWithholdingRateAsync(factory, tenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)], null, null, Billing: await BillingEditAsync(client),
                WithholdingTaxRateIds: [retefuente]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        var party = Assert.Single(saved.Order.Parties, candidate => candidate.Role == "Billing");
        Assert.Equal(("Otra S.A.S.", "Juridica", "NIT"), (party.Name, party.PersonType, party.IdentificationType));
        Assert.True(saved.Order.RetentionAmount > 0m);
        // La versión es token de concurrencia (spec 2026-09-17, decisión 6), no un contador de guardados: cada
        // mutación del agregado la sube (SetBilling y RecalculatePaymentStatus en este guardado), así que lo que
        // se exige es que suba y que la respuesta traiga la que quedó guardada, como en
        // SaveAppliesProductsProofsAndNotesTogetherAndReturnsTheNewVersion.
        Assert.True(saved.Order.Version > order.Version);
        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(saved.Order.Version, fetched.Order.Version);
        Assert.Equal(saved.Order.RetentionAmount, fetched.Order.RetentionAmount);
    }

    // Task 1B de la tienda publica: un pedido no edita su parte de envio (solo la facturacion, ver el DTO), asi que el
    // editor acepta la parte foranea por el bloque de facturacion; la de envio llega ya foranea desde la cotizacion.
    [Fact]
    public async Task TheOrderEditorAcceptsAForeignShippingParty()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        // Natural: en un tenant colombiano una jurídica exige NIT aunque la parte sea de afuera (BillingPartyFiscalRules.cs:18-35).
        var foreign = new OrderBillingEditRequest(
            new QuotationPartyRequest(
                "Lucía Fernández", "X1234567L", "+34600111222", "fact@madrid.es", "Calle Mayor 1", null, null,
                PersonType: "Natural", IdentificationType: "NIE", Country: "ES", CityName: "Madrid"),
            BillingWithRetention: false,
            BillingVatSurplus: false);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null, Billing: foreign));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var party = Assert.Single((await ReadDetailAsync(response)).Order.Parties, candidate => candidate.Role == "Billing");
        Assert.Equal(("ES", "Madrid", null, null), (party.Country, party.CityName, party.DepartmentId, party.CityId));
        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal("ES", Assert.Single(fetched.Order.Parties, candidate => candidate.Role == "Billing").Country);
    }

    [Fact]
    public async Task AnOrderBillingPartyWithACcAbroadIsA422OnItsType()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var abroad = new OrderBillingEditRequest(
            new QuotationPartyRequest(
                "Lucía Fernández", "X1234567L", "+34600111222", "fact@madrid.es", "Calle Mayor 1", null, null,
                PersonType: "Natural", IdentificationType: "CC", Country: "ES", CityName: "Madrid"),
            BillingWithRetention: false,
            BillingVatSurplus: false);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null, Billing: abroad));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Billing.Party.IdentificationType", (await ReadProblemAsync(response)).Errors!.Keys);
    }

    // Cubre IX_order_parties_order_role: la segunda edición modifica la fila, no la borra y la vuelve a crear.
    [Fact]
    public async Task EditingAnExistingBillingRowKeepsASingleRow()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var items = new[] { new OrderEditItemRequest(productId, 1m) };

        var first = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(items, null, null, Billing: await BillingEditAsync(client)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstDetail = await ReadDetailAsync(first);
        var second = await PutEditsAsync(
            client, tenantId, order.Id, firstDetail.Order.Version,
            new SaveOrderEditsRequest(items, null, null, Billing: await BillingEditAsync(client, name: "Tercera S.A.S.")));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var detail = await GetDetailAsync(client, tenantId, order.Id);
        var party = Assert.Single(detail.Order.Parties, candidate => candidate.Role == "Billing");
        Assert.Equal("Tercera S.A.S.", party.Name);
    }

    [Fact]
    public async Task AnAbsentBillingLeavesItUntouched()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var items = new[] { new OrderEditItemRequest(productId, 1m) };
        var withBilling = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(items, null, null, Billing: await BillingEditAsync(client)));
        var billed = await ReadDetailAsync(withBilling);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, billed.Order.Version,
            new SaveOrderEditsRequest(items, null, "Una nota"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ReadDetailAsync(response);
        var party = Assert.Single(detail.Order.Parties, candidate => candidate.Role == "Billing");
        Assert.Equal("Otra S.A.S.", party.Name);
        Assert.Equal(billed.Order.RetentionAmount, detail.Order.RetentionAmount);
    }

    [Fact]
    public async Task JuridicaWithCcIsA422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)], null, null,
                Billing: await BillingEditAsync(client, identificationType: "CC")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotations.party.person_type_requires_nit", (await ReadProblemAsync(response)).Code);
    }

    [Fact]
    public async Task AnInvoicedOrderCannotChangeItsBilling()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ApproverPermissions, OrdersPermissions.OrderInvoice]);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/mark-paid", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var invoice = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/invoice", null, TestContext.Current.CancellationToken);
        invoice.EnsureSuccessStatusCode();
        var invoiced = await invoice.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(invoiced);

        var response = await PutEditsAsync(
            client, tenantId, invoiced.Id, invoiced.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)], null, null, Billing: await BillingEditAsync(client)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_editable", (await ReadProblemAsync(response)).Code);
    }

    // Decisión del pre-vuelo: el preview aplica el bloque billing antes de recalcular, así que la retención que
    // muestra es la que el guardado dejaría; y no persiste nada.
    [Fact]
    public async Task PreviewAppliesTheBillingBlockWithoutPersistingIt()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var items = new[] { new OrderEditItemRequest(productId, 1m) };
        var billing = await BillingEditAsync(client);
        var retefuente = await InsertWithholdingRateAsync(factory, tenantId, "Retefuente 2,5 %", "Retefuente", 2.5m);
        var body = new SaveOrderEditsRequest(items, null, null, Billing: billing, WithholdingTaxRateIds: [retefuente]);

        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewed = await ReadDetailAsync(preview);
        var saved = await PutEditsAsync(client, tenantId, order.Id, order.Version, body);

        Assert.True(previewed.Order.RetentionAmount > 0m);
        Assert.Equal(order.Version, previewed.Order.Version);
        Assert.Equal(
            (await ReadDetailAsync(saved)).Order.RetentionAmount, previewed.Order.RetentionAmount);
    }

    // El preview se hace mientras la persona teclea: una fila a medias y sin perfil fiscal no es un 422.
    [Fact]
    public async Task PreviewAcceptsAnIncompleteBillingRow()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview",
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)], null, null,
                Billing: new OrderBillingEditRequest(
                    new QuotationPartyRequest("Otra S.A.S.", null, null, null, null, null, null, PersonType: "Juridica"))),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    // Para el caso de «sin persistir»: antes del guardado el pedido sigue sin fila ni retención.
    [Fact]
    public async Task PreviewWithBillingLeavesTheStoredOrderWithoutTheRow()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview",
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)], null, null, Billing: await BillingEditAsync(client)),
            TestContext.Current.CancellationToken);
        preview.EnsureSuccessStatusCode();

        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.DoesNotContain(fetched.Order.Parties, candidate => candidate.Role == "Billing");
        Assert.Equal(0m, fetched.Order.RetentionAmount);
        Assert.Equal(order.Version, fetched.Order.Version);
    }

    /// <summary>Una cotización enviada con un producto de 100.000 COP sin impuesto, convertida con el
    /// pago pendiente y sin comprobantes: el total es 100.000 y el estado de pago lo decide cada
    /// prueba. Con <paramref name="withRetention"/> el cliente tiene asignada una Retefuente del 2,5 % (spec
    /// impuestos por tipo §7.4), la cotización la congela y el pedido la hereda: el neto a cobrar queda por
    /// debajo del total.</summary>
    private static async Task<(QuotationResponse Quotation, OrderResponse Order, Guid ProductId)> CreatePendingOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, bool withRetention = false)
    {
        IReadOnlyList<Guid>? rates = withRetention
            ? [await InsertWithholdingRateAsync(factory, tenantId, "Retefuente 2,5 %", "Retefuente", 2.5m)]
            : null;
        var clientId = await CreateActiveCustomerAsync(client, tenantId, withholdingTaxRateIds: rates);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return (quotation, order, productId);
    }

    // Un pedido con un comprobante por el total, marcado pagado por quien tiene approve.
    private static async Task<(OrderResponse Order, Guid ProductId, Guid ProofId)> CreatePaidOrderWithProofAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var converted = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, [new OrderPaymentProofRequest(proofFileId, quotation.Total)]),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        using var approver = CreateClient(factory, ownerUserId.ToString(), tenantId.ToString(), ApproverPermissions);
        var paid = await approver.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/mark-paid", null, TestContext.Current.CancellationToken);
        paid.EnsureSuccessStatusCode();
        var detail = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal("FullPaymentReceived", detail.Order.PaymentStatus);
        return (detail.Order, productId, Assert.Single(detail.Order.PaymentProofs).Id);
    }

    private static async Task<HttpResponseMessage> PutEditsAsync(
        HttpClient client, Guid tenantId, Guid orderId, long? version, SaveOrderEditsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, OrderByIdUrl(tenantId, orderId))
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
            OrderByIdUrl(tenantId, orderId), TestContext.Current.CancellationToken);
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

    private sealed record ProblemPayload(string Code, Dictionary<string, string[]>? Errors = null);
}
