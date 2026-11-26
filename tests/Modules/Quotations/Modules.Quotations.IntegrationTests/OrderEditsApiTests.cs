using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-09-17: editar un pedido como borrador. El guardado atómico
/// (<c>PUT /orders/{orderId}</c> con <c>If-Match</c>) y el cálculo previo
/// (<c>POST /orders/{orderId}/preview</c>), contra Postgres real.
/// </summary>
public sealed class OrderEditsApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    // El literal del contrato: Shopify lo lee por nombre (ShopifyDeliveryEditPushTests).
    private const string ShippingPartyChanged = "quotations.order.shipping-party-changed.v1";

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

    // Perfiles de facturación (spec 2026-11-24 §3.6): la cotización y el pedido los leen en `client` para copiarlos, con la
    // misma forma y en el mismo orden. Se leen en vivo de la ficha, así que sin perfiles viajan vacíos y no ausentes.
    [Fact]
    public async Task TheQuotationAndOrderDetailsCarryTheClientBillingProfiles()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (quotation, order, _) = await CreatePendingOrderAsync(client, factory, tenantId);

        async Task<QuotationClientResponse> QuotationClientAsync() =>
            (await client.GetFromJsonAsync<QuotationResponse>(
                $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken))!.Client!;

        var emptyQuotation = await QuotationClientAsync();
        var emptyOrder = await GetDetailAsync(client, tenantId, order.Id);

        Assert.Empty(emptyQuotation.BillingProfiles);
        Assert.Equal(50, emptyQuotation.BillingProfileLimit);
        Assert.Empty(emptyOrder.Client!.BillingProfiles);
        Assert.Equal(50, emptyOrder.Client.BillingProfileLimit);

        var (home, _) = await EnsureCityIdsInTwoDepartmentsAsync(client);
        var madrid = await MadridRegionIdAsync(client);
        var rate = await InsertWithholdingRateAsync(factory, tenantId, "Retefuente del perfil", "Retefuente", 2.5m);
        object[] profiles =
        [
            new
            {
                name = "Zeta SAS", identificationType = "NIT", identificationNumber = "900123456",
                address = "Cra 43A # 1-50", cityId = home.CityId, vatSurplus = true
            },
            new
            {
                name = "Andina Iberia SL", identificationType = "NIF", identificationNumber = "B12345678",
                address = "Calle Mayor 1", country = "ES", cityName = "Madrid", regionId = madrid,
                withholdingTaxRateIds = new[] { rate }
            },
            new
            {
                name = "Beta SAS", identificationType = "NIT", identificationNumber = "900123457",
                address = "Calle 1 # 2-3", cityId = home.CityId
            }
        ];
        foreach (var profile in profiles)
        {
            var posted = await client.PostAsJsonAsync(
                $"/api/v1/tenants/{tenantId}/customers/{quotation.ClientId}/billing-profiles",
                profile,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        }

        var fromQuotation = await QuotationClientAsync();
        var fromOrder = (await GetDetailAsync(client, tenantId, order.Id)).Client!;

        Assert.Equal(50, fromQuotation.BillingProfileLimit);
        Assert.Equal(50, fromOrder.BillingProfileLimit);
        // La principal (la primera guardada) y después por nombre.
        Assert.Equal(
            ["Zeta SAS", "Andina Iberia SL", "Beta SAS"], fromQuotation.BillingProfiles.Select(profile => profile.Name));
        Assert.Equal(
            JsonSerializer.Serialize(fromQuotation.BillingProfiles), JsonSerializer.Serialize(fromOrder.BillingProfiles));
        var principal = fromOrder.BillingProfiles[0];
        Assert.True(principal.IsPrincipal);
        Assert.True(principal.VatSurplus);
        Assert.Equal(home.CityId, principal.CityId);
        Assert.Equal(home.DepartmentId, principal.DepartmentId);
        Assert.False(string.IsNullOrWhiteSpace(principal.CityName));
        Assert.False(string.IsNullOrWhiteSpace(principal.DepartmentName));
        var spanish = fromOrder.BillingProfiles[1];
        Assert.Equal(new QuotationRegionDto(madrid, "Madrid"), spanish.Region);
        Assert.Equal("Madrid", spanish.CityName);
        Assert.Null(spanish.CityId);
        Assert.Null(spanish.DepartmentId);
        var withholding = Assert.Single(spanish.WithholdingTaxRates);
        Assert.Equal(
            new QuotationClientWithholdingRateResponse(rate, "Retefuente del perfil", "Retefuente", "CO", 2.5m, true, "All"),
            withholding);
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

    // Spec 2026-11-16 §3 y §4 (F-4, F-6): la fecha del pedido se edita en el guardado, sube la versión y deja una entrada
    // OrderDateChanged con la fecha anterior y la nueva. El 2026-11-10 ya pasó, así que siempre es una fecha aceptada.
    [Fact]
    public async Task SavingAnOrderDateChangesItAndRecordsHistory()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var moved = new DateOnly(2026, 11, 10);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null, OrderDate: moved));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(moved, saved.Order.OrderDate);
        Assert.True(saved.Order.Version > order.Version);
        var entry = Assert.Single(saved.History, history => history.EventType == "OrderDateChanged");
        Assert.Equal($"Cambió la fecha del pedido de {order.OrderDate:yyyy-MM-dd} a 2026-11-10.", entry.Details);
        Assert.Equal(moved, (await GetDetailAsync(client, tenantId, order.Id)).Order.OrderDate);

        // Owner, 2026-11-16: además del historial, el cambio de fecha queda en la auditoría.
        var auditActions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(ActionOf)
            .ToArray();
        Assert.Contains("quotation.order.order_date_changed", auditActions);
    }

    // §4: sin `orderDate` en el cuerpo (un frontend viejo) la fecha guardada no se toca.
    [Fact]
    public async Task SavingWithoutOrderDateKeepsIt()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 2m)], null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(order.OrderDate, saved.Order.OrderDate);
        Assert.DoesNotContain(saved.History, history => history.EventType == "OrderDateChanged");
    }

    // §3: la misma fecha no es un cambio: ni historial ni versión nueva.
    [Fact]
    public async Task SavingTheSameOrderDateRecordsNothing()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null, OrderDate: order.OrderDate));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(order.Version, saved.Order.Version);
        Assert.DoesNotContain(saved.History, history => history.EventType == "OrderDateChanged");
        Assert.DoesNotContain(
            "quotation.order.order_date_changed",
            (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Select(ActionOf));
    }

    // F-5: el tope es hoy en el calendario del tenant. Dos días después de hoy en Bogotá (la zona del tenant de prueba) es
    // futuro aunque la prueba cruce la medianoche. Código de dominio, sin mapa `errors`.
    [Fact]
    public async Task AFutureOrderDateIsUnprocessable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)], null, null, OrderDate: TodayInBogota().AddDays(2)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("order.order_date.in_future", problem.Code);
        Assert.Null(problem.Errors);
        Assert.Equal(order.OrderDate, (await GetDetailAsync(client, tenantId, order.Id)).Order.OrderDate);
    }

    // §4: el preview muestra la fecha nueva y no la guarda.
    [Fact]
    public async Task PreviewingAnOrderDateDoesNotPersistIt()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var moved = new DateOnly(2026, 11, 10);

        var response = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/preview",
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, null, OrderDate: moved),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadDetailAsync(response);
        Assert.Equal(moved, preview.Order.OrderDate);
        Assert.Equal(order.Version, preview.Order.Version);
        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(order.OrderDate, fetched.Order.OrderDate);
        Assert.DoesNotContain(fetched.History, history => history.EventType == "OrderDateChanged");
    }

    // F-4: preparado congela el pedido, también su fecha. Responde lo mismo que cualquier otro cambio del borrador.
    [Fact]
    public async Task APreparedOrderCannotChangeItsDate()
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
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)], null, null, OrderDate: new DateOnly(2026, 11, 10)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.not_editable", (await ReadProblemAsync(response)).Code);
        Assert.Equal(order.OrderDate, (await GetDetailAsync(client, tenantId, order.Id)).Order.OrderDate);
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

    // Task 1B de la tienda publica: el editor acepta una parte foranea por el bloque de facturacion; la de envio se prueba
    // con la entrega editable, mas abajo.
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

    // Provincia guardada (spec 2026-11-23): el PUT reemplaza la parte entera, así que guardar sin `regionId` borra la
    // provincia guardada, igual que `cityName`. Se lee del JSON de la respuesta, no del DTO.
    [Fact]
    public async Task SavingBillingWithoutRegionClearsIt()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var madrid = await MadridRegionIdAsync(client);
        OrderBillingEditRequest Foreign(Guid? regionId) => new(
            new QuotationPartyRequest(
                "Lucía Fernández", "X1234567L", "+34600111222", "fact@madrid.es", "Calle Mayor 1", null, null,
                PersonType: "Natural", IdentificationType: "NIE", Country: "ES", CityName: "Madrid", RegionId: regionId),
            BillingWithRetention: false,
            BillingVatSurplus: false);
        var items = new[] { new OrderEditItemRequest(productId, 1m) };

        var first = await PutEditsAsync(
            client, tenantId, order.Id, order.Version, new SaveOrderEditsRequest(items, null, null, Billing: Foreign(madrid)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstDetail = await ReadDetailAsync(first);
        var firstJson = await GetRawDetailAsync(client, tenantId, order.Id);
        Assert.Equal("Madrid", BillingJson(firstJson).GetProperty("region").GetProperty("name").GetString());

        var second = await PutEditsAsync(
            client, tenantId, order.Id, firstDetail.Order.Version,
            new SaveOrderEditsRequest(items, null, null, Billing: Foreign(null)));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, BillingJson(secondJson).GetProperty("region").ValueKind);
    }

    private static async Task<JsonElement> GetRawDetailAsync(HttpClient client, Guid tenantId, Guid orderId) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/tenants/{tenantId}/orders/{orderId}", TestContext.Current.CancellationToken);

    private static JsonElement BillingJson(JsonElement detail) =>
        detail.GetProperty("order").GetProperty("parties").EnumerateArray()
            .Single(party => party.GetProperty("role").GetString() == "Billing");

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

    // ----- Entrega editable (spec 2026-11-24 §10) -----

    // R-1: con `delivery` en el cuerpo, el motivo exacto gana a `not_editable`, en el guardado y en el cálculo previo.
    [Fact]
    public async Task AnInvoicedOrderAnswersAlreadyInvoicedForADelivery()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ApproverPermissions, OrdersPermissions.OrderInvoice]);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        // Preparado y además facturado: gana facturado.
        foreach (var action in new[] { "mark-paid", "prepare", "invoice" })
        {
            (await client.PostAsync(
                $"{OrderByIdUrl(tenantId, order.Id)}/{action}", null, TestContext.Current.CancellationToken))
                .EnsureSuccessStatusCode();
        }

        var invoiced = (await GetDetailAsync(client, tenantId, order.Id)).Order;
        var body = DeliveryBody(productId, new OrderDeliveryEditRequest(true, null));

        var save = await PutEditsAsync(client, tenantId, order.Id, invoiced.Version, body);
        var preview = await PreviewEditsAsync(client, tenantId, order.Id, body);

        await AssertProblemAsync(save, "order.order.already_invoiced");
        await AssertProblemAsync(preview, "order.order.already_invoiced");
    }

    [Fact]
    public async Task APreparedOrderAnswersAlreadyPreparedForADelivery()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ApproverPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/prepare", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var prepared = (await GetDetailAsync(client, tenantId, order.Id)).Order;
        var body = DeliveryBody(productId, new OrderDeliveryEditRequest(true, null));

        var save = await PutEditsAsync(client, tenantId, order.Id, prepared.Version, body);
        var preview = await PreviewEditsAsync(client, tenantId, order.Id, body);

        await AssertProblemAsync(save, "order.order.already_prepared");
        await AssertProblemAsync(preview, "order.order.already_prepared");
    }

    // Un frontend viejo no manda `delivery`: recoger o enviar y la parte quedan como estaban, sin historial ni auditoría de
    // entrega.
    [Fact]
    public async Task ABodyWithoutDeliveryKeepsIt()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (party, _) = await ColombianShippingPartyAsync(client);
        var (order, productId) = await ConvertPendingOrderAsync(
            client, factory, tenantId, new QuotationPartiesRequest(null, party));

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest([new OrderEditItemRequest(productId, 1m)], null, "Una nota"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.False(saved.Order.IsStorePickup);
        Assert.Equal(
            Assert.Single(order.Parties, candidate => candidate.Role == "Shipping"),
            Assert.Single(saved.Order.Parties, candidate => candidate.Role == "Shipping"));
        Assert.DoesNotContain(saved.History, entry => entry.EventType == "PartiesChanged");
        Assert.DoesNotContain("quotation.order.delivery_changed", await AuditActionsAsync(factory));
    }

    // Lo mismo que ya está guardado no es un cambio: ni versión, ni historial, ni auditoría.
    [Fact]
    public async Task TheSameDeliveryRecordsNothing()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (party, _) = await ColombianShippingPartyAsync(client);
        var (order, productId) = await ConvertPendingOrderAsync(
            client, factory, tenantId, new QuotationPartiesRequest(null, party));

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            DeliveryBody(productId, new OrderDeliveryEditRequest(false, party)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await ReadDetailAsync(response);
        Assert.Equal(order.Version, saved.Order.Version);
        Assert.DoesNotContain(saved.History, entry => entry.EventType == "PartiesChanged");
        Assert.DoesNotContain("quotation.order.delivery_changed", await AuditActionsAsync(factory));
    }

    [Fact]
    public async Task SwitchingToPickupDropsPartyAndFreightAndRecalculates()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, "tenancy.settings.read", "tenancy.settings.update"]);
        using var _ = client;
        var taxRateId = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);
        (await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/carriers/freight-tax-rate",
            new { taxRateId }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var (party, cityName) = await ColombianShippingPartyAsync(client);
        var (order, productId) = await ConvertPendingOrderAsync(
            client, factory, tenantId, new QuotationPartiesRequest(null, party));
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        // Primero, un envío cobrado y un comprobante que no alcanza para el neto con el envío.
        var charged = await ReadDetailAsync(await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, 100_000m)], null, null), null,
                Shipping: new ShippingRequest(null, "Charged", 15_000m, false))));
        Assert.Equal(("PartialPaymentReceived", 115_000m), (charged.Order.PaymentStatus, charged.Order.Total));
        var body = DeliveryBody(productId, new OrderDeliveryEditRequest(true, null));

        var preview = await PreviewEditsAsync(client, tenantId, order.Id, body);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewed = (await ReadDetailAsync(preview)).Order;
        Assert.Equal((true, 100_000m, 0m), (previewed.IsStorePickup, previewed.Total, previewed.Shipping.FreightAmount));
        Assert.Equal(charged.Order.Version, previewed.Version);
        Assert.DoesNotContain("quotation.order.delivery_changed", await AuditActionsAsync(factory));

        var response = await PutEditsAsync(client, tenantId, order.Id, charged.Order.Version, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReadDetailAsync(response);
        var saved = await GetDetailAsync(client, tenantId, order.Id);
        Assert.True(saved.Order.IsStorePickup);
        Assert.DoesNotContain(saved.Order.Parties, candidate => candidate.Role == "Shipping");
        Assert.Null(saved.Order.Shipping.ChargeMode);
        Assert.Equal((0m, 0m, 0m), (saved.Order.Shipping.FreightAmount, saved.Order.FreightSubtotal, saved.Order.FreightTaxAmount));
        Assert.Equal(100_000m, saved.Order.Total);
        Assert.Equal("PendingVerification", saved.Order.PaymentStatus);
        var partiesChanged = saved.History.Where(entry => entry.EventType == "PartiesChanged")
            .Select(entry => entry.Details).ToArray();
        Assert.Contains($"Entrega: Enviar a Cra 7 # 12-30, {cityName} → Recoger en tienda", partiesChanged);
        // La entrega y el envío cambiaron en el mismo guardado: salen las dos filas (la tercera es el envío del primero).
        Assert.Equal(3, partiesChanged.Length);
        var actions = await AuditActionsAsync(factory);
        Assert.Single(actions, action => action == "quotation.order.delivery_changed");
        Assert.Equal(2, actions.Count(action => action == "quotation.order.shipping_changed"));
    }

    [Fact]
    public async Task SwitchingToSendWithASpanishPartyKeepsItsRegion()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (order, productId) = await ConvertPendingOrderAsync(
            client, factory, tenantId, new QuotationPartiesRequest(null, null, IsStorePickup: true));
        var madrid = await MadridRegionIdAsync(client);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            DeliveryBody(productId, new OrderDeliveryEditRequest(false, SpanishShippingParty("ES", madrid))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReadDetailAsync(response);
        var saved = await GetDetailAsync(client, tenantId, order.Id);
        Assert.False(saved.Order.IsStorePickup);
        var shipping = Assert.Single(saved.Order.Parties, candidate => candidate.Role == "Shipping");
        Assert.Equal(("ES", "Madrid", "28001"), (shipping.Country, shipping.CityName, shipping.PostalCode));
        Assert.Equal(new QuotationRegionDto(madrid, "Madrid"), shipping.Region);
        var entry = Assert.Single(saved.History, history => history.EventType == "PartiesChanged");
        Assert.Equal("Entrega: Recoger en tienda → Enviar a Calle Mayor 1, Madrid", entry.Details);
    }

    // De enviar a enviar: la fila de envío se modifica, no se borra y se vuelve a crear (IX_order_parties_order_role).
    [Fact]
    public async Task ChangingTheShippingAddressUpdatesTheSameRow()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (party, cityName) = await ColombianShippingPartyAsync(client);
        var (order, productId) = await ConvertPendingOrderAsync(
            client, factory, tenantId, new QuotationPartiesRequest(null, party));
        var before = Assert.Single(order.Parties, candidate => candidate.Role == "Shipping");

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            DeliveryBody(productId, new OrderDeliveryEditRequest(false, party with { Address = "Calle 1 # 2-3" })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReadDetailAsync(response);
        var saved = await GetDetailAsync(client, tenantId, order.Id);
        Assert.False(saved.Order.IsStorePickup);
        var after = Assert.Single(saved.Order.Parties, candidate => candidate.Role == "Shipping");
        Assert.Equal((before.Id, "Calle 1 # 2-3"), (after.Id, after.Address));
        Assert.True(saved.Order.Version > order.Version);
        var entry = Assert.Single(saved.History, history => history.EventType == "PartiesChanged");
        Assert.Equal(
            $"Entrega: Enviar a Cra 7 # 12-30, {cityName} → Enviar a Calle 1 # 2-3, {cityName}", entry.Details);
        Assert.Single(await AuditActionsAsync(factory), action => action == "quotation.order.delivery_changed");
    }

    // Guardar exige la parte completa, como crear el pedido y como los escritores externos.
    [Fact]
    public async Task SavingAnIncompleteShippingPartyIsRejected()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var empty = new QuotationPartyRequest(null, null, null, null, null, null, null);

        var response = await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            DeliveryBody(productId, new OrderDeliveryEditRequest(false, empty)));

        await AssertProblemAsync(response, "order.shipping.party_incomplete");
        var fetched = await GetDetailAsync(client, tenantId, order.Id);
        Assert.DoesNotContain(fetched.Order.Parties, candidate => candidate.Role == "Shipping");
        Assert.Equal(order.Version, fetched.Order.Version);
    }

    // El cálculo previo se hace mientras la persona teclea: una parte vacía o a medias no es un 422.
    [Fact]
    public async Task PreviewAcceptsAnIncompleteShippingParty()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var empty = new QuotationPartyRequest(null, null, null, null, null, null, null);

        foreach (var party in new[] { empty, empty with { Name = "Bodega Norte" } })
        {
            var preview = await PreviewEditsAsync(
                client, tenantId, order.Id, DeliveryBody(productId, new OrderDeliveryEditRequest(false, party)));

            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var previewed = (await ReadDetailAsync(preview)).Order;
            Assert.Equal(
                party.Name, Assert.Single(previewed.Parties, candidate => candidate.Role == "Shipping").Name);
        }

        Assert.DoesNotContain(
            (await GetDetailAsync(client, tenantId, order.Id)).Order.Parties, candidate => candidate.Role == "Shipping");
    }

    // R-3: la región la valida PartyRegionRules con la clave de la entrega.
    [Fact]
    public async Task ARegionOfAnotherCountryIsRejectedWithTheDeliveryKey()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var body = DeliveryBody(
            productId,
            new OrderDeliveryEditRequest(false, SpanishShippingParty("FR", await MadridRegionIdAsync(client))));

        var save = await PutEditsAsync(client, tenantId, order.Id, order.Version, body);
        var preview = await PreviewEditsAsync(client, tenantId, order.Id, body);

        foreach (var response in new[] { save, preview })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await ReadProblemAsync(response);
            Assert.Equal("quotations.party.region_invalid", problem.Code);
            Assert.Contains("Delivery.Shipping.RegionId", problem.Errors!.Keys);
        }
    }

    [Fact]
    public async Task SwitchingToSendWithoutPartyAndCustomerWithoutAddressIsRejected()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (order, productId) = await ConvertPendingOrderAsync(
            client, factory, tenantId, new QuotationPartiesRequest(null, null, IsStorePickup: true));
        // El alta pide la dirección según la política del tenant: un cliente sin domicilio se arma por SQL.
        Assert.Equal(1, await ExecuteSqlAsync(
            factory, $"UPDATE customers.customers SET address = NULL WHERE id = '{order.ClientId}'"));
        var body = DeliveryBody(productId, new OrderDeliveryEditRequest(false, null));

        var save = await PutEditsAsync(client, tenantId, order.Id, order.Version, body);
        var preview = await PreviewEditsAsync(client, tenantId, order.Id, body);

        await AssertProblemAsync(save, "order.delivery.address_required");
        await AssertProblemAsync(preview, "order.delivery.address_required");
        Assert.True((await GetDetailAsync(client, tenantId, order.Id)).Order.IsStorePickup);
    }

    [Fact]
    public async Task PickupWithAShippingPartyIsRejected()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (_, order, productId) = await CreatePendingOrderAsync(client, factory, tenantId);
        var (party, _) = await ColombianShippingPartyAsync(client);
        var body = DeliveryBody(productId, new OrderDeliveryEditRequest(true, party));

        var save = await PutEditsAsync(client, tenantId, order.Id, order.Version, body);
        var preview = await PreviewEditsAsync(client, tenantId, order.Id, body);

        await AssertProblemAsync(save, "order.delivery.shipping_party_on_pickup");
        await AssertProblemAsync(preview, "order.delivery.shipping_party_on_pickup");
    }

    // El envío se resuelve después de la entrega, ya sin recogida, y la regla de la contraentrega sigue siendo la misma.
    [Fact]
    public async Task CashOnDeliveryWithProofsAfterSwitchingToSendIsRejected()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (order, productId) = await ConvertPendingOrderAsync(
            client, factory, tenantId, new QuotationPartiesRequest(null, null, IsStorePickup: true));
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var withProof = await ReadDetailAsync(await PutEditsAsync(
            client, tenantId, order.Id, order.Version,
            new SaveOrderEditsRequest(
                [new OrderEditItemRequest(productId, 1m)],
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, 50_000m)], null, null), null)));

        var response = await PutEditsAsync(
            client, tenantId, order.Id, withProof.Order.Version,
            DeliveryBody(
                productId, new OrderDeliveryEditRequest(false, null), new ShippingRequest(null, null, 0m, true)));

        await AssertProblemAsync(response, "order.order.cash_on_delivery_pending_only");
        Assert.True((await GetDetailAsync(client, tenantId, order.Id)).Order.IsStorePickup);
    }

    // La bandera no depende del origen (owner 2026-11-24): sólo de que el pedido siga editable.
    [Fact]
    public async Task TheDetailSaysWhetherTheDeliveryIsEditable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(
            factory, [.. ApproverPermissions, OrdersPermissions.OrderInvoice]);
        using var _ = client;
        var (_, converted, _) = await CreatePendingOrderAsync(client, factory, tenantId);
        var manualId = await CreateManualOrderAsync(client, tenantId);
        var shopifyId = await CreateShopifyOrderAsync(client, factory, tenantId, ownerUserId);
        var (wholesaleId, _) = await CreateWholesaleOrderAsync(client, factory, tenantId, ownerUserId);

        foreach (var orderId in new[] { converted.Id, manualId, shopifyId, wholesaleId })
        {
            var raw = await GetRawDetailAsync(client, tenantId, orderId);
            Assert.True(raw.GetProperty("order").GetProperty("deliveryEditable").GetBoolean(), $"Order {orderId}");
        }

        async Task ActAsync(Guid orderId, string action)
        {
            var response = await client.PostAsync(
                $"{OrderByIdUrl(tenantId, orderId)}/{action}", null, TestContext.Current.CancellationToken);
            Assert.True(
                response.IsSuccessStatusCode,
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await ActAsync(converted.Id, "mark-paid");
        await ActAsync(converted.Id, "invoice");
        await ActAsync(manualId, "prepare");
        await ActAsync(wholesaleId, "approve");

        foreach (var orderId in new[] { converted.Id, manualId, wholesaleId })
        {
            var raw = await GetRawDetailAsync(client, tenantId, orderId);
            Assert.False(raw.GetProperty("order").GetProperty("deliveryEditable").GetBoolean(), $"Order {orderId}");
        }

        Assert.True((await GetDetailAsync(client, tenantId, shopifyId)).Order.DeliveryEditable);
    }

    // Las reglas de Shopify (spec 2026-11-24 §11.1.4) son sólo de sus pedidos: uno manual manda a España sin región y vuelve a
    // los datos del cliente.
    [Fact]
    public async Task AManualOrderSkipsTheShopifyRules()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var orderId = await CreateManualOrderAsync(client, tenantId);
        var detail = await GetDetailAsync(client, tenantId, orderId);
        var productId = Assert.Single(detail.Items).VariantId;
        var spain = new QuotationPartyRequest(
            "Lucía Fernández", null, "+34600111222", "entregas@madrid.es", "Calle Mayor 1", null, null,
            Country: "ES", CityName: "Madrid");

        var toSpain = await PutEditsAsync(
            client, tenantId, orderId, detail.Order.Version,
            DeliveryBody(productId, new OrderDeliveryEditRequest(false, spain)));
        var saved = await ReadDetailAsync(toSpain);
        var toCustomer = await PutEditsAsync(
            client, tenantId, orderId, saved.Order.Version,
            DeliveryBody(productId, new OrderDeliveryEditRequest(false, null)));

        Assert.Equal(HttpStatusCode.OK, toSpain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, toCustomer.StatusCode);
        // Tampoco publica el aviso a Shopify (§11.1.1).
        Assert.Empty(await OutboxMessagesAsync(factory, ShippingPartyChanged));
    }

    // Spec 2026-11-24 §11.1.1: la dirección nueva de un pedido de Shopify sale al outbox en la transacción del pedido, y
    // Shopify encola su envío. El cálculo previo no publica y repetir la misma entrega tampoco.
    [Fact]
    public async Task ChangingAShopifyOrderAddressWritesTheOutboxEvent()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var orderId = await CreateShopifyOrderAsync(client, factory, tenantId, ownerUserId);
        var detail = await GetDetailAsync(client, tenantId, orderId);
        var (party, _) = await ColombianShippingPartyAsync(client);
        var body = DeliveryBody(Assert.Single(detail.Items).VariantId, new OrderDeliveryEditRequest(false, party));

        (await PreviewEditsAsync(client, tenantId, orderId, body)).EnsureSuccessStatusCode();
        Assert.Empty(await OutboxMessagesAsync(factory, ShippingPartyChanged));
        var saved = await ReadDetailAsync(await PutEditsAsync(client, tenantId, orderId, detail.Order.Version, body));
        (await PutEditsAsync(client, tenantId, orderId, saved.Order.Version, body)).EnsureSuccessStatusCode();

        var message = Assert.Single(await OutboxMessagesAsync(factory, ShippingPartyChanged));
        var payload = JsonDocument.Parse(message.PayloadJson).RootElement;
        Assert.Equal(
            ["occurredAt", "orderId", "source", "tenantId"],
            payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            (tenantId, orderId, "Shopify"),
            (payload.GetProperty("tenantId").GetGuid(), payload.GetProperty("orderId").GetGuid(),
                payload.GetProperty("source").GetString()));
    }

    // Recoger no cambia nada en Shopify: no hay dirección que mandar.
    [Fact]
    public async Task SwitchingAShopifyOrderToPickupPublishesNothing()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var orderId = await CreateShopifyOrderAsync(client, factory, tenantId, ownerUserId);
        var detail = await GetDetailAsync(client, tenantId, orderId);

        var response = await PutEditsAsync(
            client, tenantId, orderId, detail.Order.Version,
            DeliveryBody(Assert.Single(detail.Items).VariantId, new OrderDeliveryEditRequest(true, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await GetDetailAsync(client, tenantId, orderId)).Order.IsStorePickup);
        Assert.Empty(await OutboxMessagesAsync(factory, ShippingPartyChanged));
    }

    [Fact]
    public async Task AWholesaleOrderAwaitingApprovalChangesItsDelivery()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (orderId, productId) = await CreateWholesaleOrderAsync(client, factory, tenantId, ownerUserId);
        var order = (await GetDetailAsync(client, tenantId, orderId)).Order;

        var response = await PutEditsAsync(
            client, tenantId, orderId, order.Version, DeliveryBody(productId, new OrderDeliveryEditRequest(true, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ReadDetailAsync(response);
        var saved = await GetDetailAsync(client, tenantId, orderId);
        Assert.True(saved.Order.IsStorePickup);
        var entry = Assert.Single(saved.History, history => history.EventType == "PartiesChanged");
        Assert.Equal("Entrega: Enviar a la dirección del cliente → Recoger en tienda", entry.Details);
    }

    [Fact]
    public async Task AnApprovedWholesaleOrderAnswersNotEditable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var (orderId, productId) = await CreateWholesaleOrderAsync(client, factory, tenantId, ownerUserId);
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, orderId)}/approve", null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var approved = (await GetDetailAsync(client, tenantId, orderId)).Order;
        var body = DeliveryBody(productId, new OrderDeliveryEditRequest(true, null));

        var save = await PutEditsAsync(client, tenantId, orderId, approved.Version, body);
        var preview = await PreviewEditsAsync(client, tenantId, orderId, body);

        await AssertProblemAsync(save, "order.order.not_editable");
        await AssertProblemAsync(preview, "order.order.not_editable");
    }

    private static SaveOrderEditsRequest DeliveryBody(
        Guid productId, OrderDeliveryEditRequest delivery, ShippingRequest? shipping = null) =>
        new([new OrderEditItemRequest(productId, 1m)], null, null, Shipping: shipping) { Delivery = delivery };

    /// <summary>Una parte de envío colombiana completa y el nombre DIVIPOLA de su ciudad, que es el que cuenta el
    /// historial.</summary>
    private static async Task<(QuotationPartyRequest Party, string CityName)> ColombianShippingPartyAsync(HttpClient client)
    {
        var home = (await EnsureCityIdsInTwoDepartmentsAsync(client)).Second;
        var cities = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/cities?departmentId={home.DepartmentId}", TestContext.Current.CancellationToken);
        var cityName = cities.EnumerateArray()
            .Single(city => city.GetProperty("id").GetGuid() == home.CityId)
            .GetProperty("name").GetString()!;
        return (
            new QuotationPartyRequest(
                "Bodega Norte", null, "3001234567", "bodega@correo.co", "Cra 7 # 12-30", home.DepartmentId, home.CityId),
            cityName);
    }

    private static QuotationPartyRequest SpanishShippingParty(string country, Guid regionId) =>
        new(
            "Lucía Fernández", null, "+34600111222", "entregas@madrid.es", "Calle Mayor 1", null, null,
            Country: country, CityName: "Madrid", RegionId: regionId, PostalCode: "28001");

    /// <summary>Como <see cref="CreatePendingOrderAsync"/>, con las partes dadas en la cotización de origen.</summary>
    private static async Task<(OrderResponse Order, Guid ProductId)> ConvertPendingOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, QuotationPartiesRequest parties)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId, parties: parties);
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return (order, productId);
    }

    private static async Task<Guid> CreateManualOrderAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new OrderEditItemRequest(productId, 1m)], []),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadDetailAsync(response)).Order.Id;
    }

    private static async Task<Guid> CreateShopifyOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        await using var scope = factory.Services.CreateAsyncScope();
        var advisorId = await AdvisorMembershipIdAsync(scope.ServiceProvider, tenantId, ownerUserId);
        var result = await scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>().CreateAsync(
            new ExternalOrderDraft(
                tenantId, OrderSource.Shopify, "#4001", clientId, advisorId, null, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new ExternalOrderLine(productId, productId, 1m, 11_900m, 0m, 19)],
                OrderPaymentStatus.PaymentPending, DateTimeOffset.UtcNow, ShippingDetails.None,
                $"shop-1:{Guid.CreateVersion7()}", null),
            TestContext.Current.CancellationToken);
        return result.OrderId;
    }

    /// <summary>Un pedido mayorista sin aprobar, por el puerto que usa la orden de compra vinculada.</summary>
    private static async Task<(Guid OrderId, Guid ProductId)> CreateWholesaleOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        await using var scope = factory.Services.CreateAsyncScope();
        var advisorId = await AdvisorMembershipIdAsync(scope.ServiceProvider, tenantId, ownerUserId);
        var result = await scope.ServiceProvider.GetRequiredService<IWholesaleOrderGateway>().CreateAsync(
            new WholesaleOrderDraft(
                tenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), "OC-0001", clientId, advisorId, billing.CompanyId,
                billing.Currency, null, [new WholesaleOrderLine(Guid.CreateVersion7(), productId, 1m, 90_000m)]),
            TestContext.Current.CancellationToken);
        return (result.OrderId, productId);
    }

    private static async Task<Guid> AdvisorMembershipIdAsync(IServiceProvider services, Guid tenantId, Guid userId)
    {
        var advisorId = await services.GetRequiredService<IMembershipDirectory>()
            .FindActiveMembershipIdAsync(userId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(advisorId);
        return advisorId.Value;
    }

    private static Task<HttpResponseMessage> PreviewEditsAsync(
        HttpClient client, Guid tenantId, Guid orderId, SaveOrderEditsRequest body) =>
        client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, orderId)}/preview", body, TestContext.Current.CancellationToken);

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"Expected 422, got {response.StatusCode}: {text}");
        Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }

    private static async Task<string[]> AuditActionsAsync(QepApiFactory factory) =>
        (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Select(ActionOf).ToArray();

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
