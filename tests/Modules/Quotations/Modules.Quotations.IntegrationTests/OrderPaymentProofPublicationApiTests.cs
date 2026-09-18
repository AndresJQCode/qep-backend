using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La copia pública de los comprobantes al adjuntarlos (spec 2026-09-15, P4 y P7), de punta a punta:
/// convertir y sumar comprobantes con la opción encendida y apagada, una copia que falla, un rechazo
/// del dominio después de copiar, y corregir sólo montos. La respuesta de la API no expone la clave
/// (OrderPaymentProofResponse no cambia), así que se lee de la base.
/// </summary>
public sealed class OrderPaymentProofPublicationApiTests
{
    private const string PublicKeyPattern = "^payment-proofs/[0-9a-f]{32}\\.pdf$";

    // El contrato con Storage (spec 2026-09-16, D9), escrito a mano: si alguien lo cambia de un solo
    // lado, estas pruebas lo ven.
    private const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";

    // D19: el evento de retiro, también escrito a mano.
    private const string DetachedEventName = "quotations.order.payment-proofs-detached.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    // El pedido se direcciona por su propio id: sumar, corregir o quitar comprobantes ya no
    // cuelga de la cotización.
    private static string OrderByIdUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    private static string OrderProofsUrl(Guid tenantId, Guid orderId) =>
        $"{OrderByIdUrl(tenantId, orderId)}/proofs";

    // P4: con la opción encendida, cada comprobante de la conversión tiene su copia y su clave queda
    // guardada.
    [Fact]
    public async Task ConvertingWithPublicLinksOnCopiesEachProofAndStoresItsKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var order = await ConvertAsync(
            client, tenantId, quotation.Id, "FullPaymentReceived", firstFileId, secondFileId);

        var keys = await PublicKeysAsync(factory, order.Id);
        Assert.Equal(2, keys.Length);
        Assert.All(keys, key => Assert.Matches(PublicKeyPattern, key));
        Assert.Equal(
            factory.PublicObjectStorage.Copies.Keys.Order(StringComparer.Ordinal),
            keys.Order(StringComparer.Ordinal));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
    }

    // P1: apagada, no se copia nada y el comprobante queda privado. Ya pasa antes de esta tarea:
    // protege el camino de siempre.
    [Fact]
    public async Task ConvertingWithPublicLinksOffCopiesNothingAndLeavesTheKeyEmpty()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        Assert.Null(Assert.Single(await PublicKeysAsync(factory, order.Id)));
        Assert.Empty(factory.PublicObjectStorage.Copies);
    }

    // P4: sumar comprobantes a un pedido pendiente también los copia.
    [Fact]
    public async Task AddingProofsWithPublicLinksOnCopiesTheNewProof()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = Assert.Single(await PublicKeysAsync(factory, order.Id));
        Assert.Matches(PublicKeyPattern, key);
        Assert.Equal(key, Assert.Single(factory.PublicObjectStorage.Copies).Key);
    }

    // P1: apagada, sumar comprobantes tampoco copia nada y el nuevo queda privado. Ya pasa antes de
    // esta tarea: protege el camino de siempre.
    [Fact]
    public async Task AddingProofsWithPublicLinksOffCopiesNothingAndLeavesTheKeyEmpty()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(await PublicKeysAsync(factory, order.Id)));
        Assert.Empty(factory.PublicObjectStorage.Copies);
    }

    // P4: corregir un monto no cambia el archivo: no copia nada ni toca la clave que ya había.
    [Fact]
    public async Task CorrectingOnlyAmountsCopiesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var keyBefore = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [], UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.PublicObjectStorage.Copies);
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.Equal(keyBefore, Assert.Single(await PublicKeysAsync(factory, order.Id)));
    }

    // P7: si la segunda copia falla, el request falla, el pedido no se crea y la primera copia se borra.
    [Fact]
    public async Task AConversionWhoseCopyFailsCreatesNoOrderAndDeletesTheCopiesMade()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                null,
                [new OrderPaymentProofRequest(firstFileId, 10_000m), new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Matches(PublicKeyPattern, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Empty(factory.PublicObjectStorage.Copies);
        var getOrder = await client.GetAsync(OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getOrder.StatusCode);
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Sent", fetched.Status);
    }

    // P7: lo mismo al sumar comprobantes: el pedido queda como estaba.
    [Fact]
    public async Task AddingProofsWhoseCopyFailsAddsNothingAndDeletesTheCopiesMade()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var created = await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, created.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [new OrderPaymentProofRequest(firstFileId, 10_000m), new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Matches(PublicKeyPattern, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Empty(factory.PublicObjectStorage.Copies);
        var order = await client.GetFromJsonAsync<OrderResponse>(
            OrderUrl(tenantId, quotation.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Empty(order.PaymentProofs);
        Assert.Equal("PaymentPending", order.PaymentStatus);
    }

    // P7: el rechazo del dominio llega después de copiar —acá, sumar a un pedido que otra persona
    // acaba de aprobar— y la copia de ese request se borra. La del comprobante original queda.
    [Fact]
    public async Task ADomainRejectionAfterCopyingDeletesThatCopy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", firstFileId);
        var firstKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        (await client.PostAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/approve", content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.order.not_pending", problem?.Code);
        Assert.NotEqual(firstKey, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Equal(firstKey, Assert.Single(factory.PublicObjectStorage.Copies).Key);
        Assert.Equal(firstKey, Assert.Single(await PublicKeysAsync(factory, order.Id)));
    }

    // P7 en la conversión: una segunda conversión de la misma cotización la corta el dominio
    // (status_not_convertible) después de copiar su comprobante, y esa copia se borra.
    [Fact]
    public async Task ASecondConversionDeletesTheCopyItMade()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", firstFileId);
        var firstKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("quotation.quotation.status_not_convertible", problem?.Code);
        Assert.NotEqual(firstKey, Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Equal(firstKey, Assert.Single(factory.PublicObjectStorage.Copies).Key);
    }

    // D9 (spec 2026-09-16): convertir con copias públicas deja, con el pedido, un evento con cada
    // comprobante nuevo y la clave de su copia.
    [Fact]
    public async Task ConvertingWithPublicLinksOnWritesTheAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var order = await ConvertAsync(
            client, tenantId, quotation.Id, "FullPaymentReceived", firstFileId, secondFileId);

        var message = Assert.Single(await OutboxMessagesAsync(factory, AttachedEventName));
        var payload = JsonSerializer.Deserialize<AttachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        Assert.Equal(tenantId, payload.TenantId);
        Assert.Equal(order.Id, payload.OrderId);
        Assert.Equal(
            new[] { firstFileId, secondFileId }.Order(),
            payload.Proofs.Select(proof => proof.FileId).Order());
        Assert.Equal(
            (await PublicKeysAsync(factory, order.Id)).Select(key => key!).Order(StringComparer.Ordinal),
            payload.Proofs.Select(proof => proof.PublicStorageKey).Order(StringComparer.Ordinal));
    }

    // D9: sumar comprobantes escribe otro evento, sólo con el comprobante nuevo.
    [Fact]
    public async Task AddingProofsWithPublicLinksOnWritesAnEventWithOnlyTheNewProof()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", firstFileId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messages = await OutboxMessagesAsync(factory, AttachedEventName);
        Assert.Equal(2, messages.Count);
        var added = JsonSerializer.Deserialize<AttachedEventPayload>(messages[1].PayloadJson, Json);
        Assert.NotNull(added);
        Assert.Equal(order.Id, added.OrderId);
        var proof = Assert.Single(added.Proofs);
        Assert.Equal(secondFileId, proof.FileId);
        Assert.Contains(proof.PublicStorageKey, await PublicKeysAsync(factory, order.Id));
    }

    // D9 y D19: el archivo de reemplazo de un comprobante corregido (UpdatedProofs[].NewFileId, de
    // develop) es un comprobante nuevo para Storage, así que entra en el evento con la clave de su copia.
    [Fact]
    public async Task ReplacingAProofFileWithPublicLinksOnWritesAnEventWithTheReplacement()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", firstFileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var replacementFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, replacementFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messages = await OutboxMessagesAsync(factory, AttachedEventName);
        Assert.Equal(2, messages.Count);
        var replaced = JsonSerializer.Deserialize<AttachedEventPayload>(messages[1].PayloadJson, Json);
        Assert.NotNull(replaced);
        Assert.Equal(order.Id, replaced.OrderId);
        var proof = Assert.Single(replaced.Proofs);
        Assert.Equal(replacementFileId, proof.FileId);
        Assert.Equal(Assert.Single(await PublicKeysAsync(factory, order.Id)), proof.PublicStorageKey);
    }

    // Sin copia pública no hay nada que mover: la opción apagada no escribe el evento. Ya pasa antes
    // de esta tarea.
    [Fact]
    public async Task ConvertingWithPublicLinksOffWritesNoAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        Assert.Empty(await OutboxMessagesAsync(factory, AttachedEventName));
    }

    // Corregir un monto no adjunta nada: no hay evento nuevo.
    [Fact]
    public async Task CorrectingOnlyAmountsWritesNoNewAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [], UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await OutboxMessagesAsync(factory, AttachedEventName));
    }

    // D9, «misma transacción»: un request que falla antes de guardar no deja evento. Ya pasa antes de
    // esta tarea.
    [Fact]
    public async Task AConversionWhoseCopyFailsWritesNoAttachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                null,
                [new OrderPaymentProofRequest(firstFileId, 10_000m), new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, AttachedEventName));
    }

    // D8, D9 y D3 de punta a punta (spec 2026-09-16): la imagen de un PaymentProof llega procesada, se
    // copia al público al convertir y PaymentProofMoveWorker —que corre solo en el host— borra el
    // temporal y registra el movimiento.
    [Fact]
    public async Task ConvertingWithAPaymentProofImageMovesItToThePublicBucket()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var stagingKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);

        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        await AssertMovedAsync(database.GetConnectionString(), factory, order.Id, fileId, stagingKey);
    }

    [Fact]
    public async Task AddingAPaymentProofImageMovesItToThePublicBucket()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var stagingKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertMovedAsync(database.GetConnectionString(), factory, order.Id, fileId, stagingKey);
    }

    // D9, paso 3: si algo falla antes de guardar, el rollback borra las copias públicas y los
    // temporales siguen en staging/. Ya pasa antes de esta tarea.
    [Fact]
    public async Task AConversionWhoseCopyFailsLeavesThePaymentProofsInStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var firstStagingKey = await StorageKeyOfAsync(database.GetConnectionString(), firstFileId);
        var secondStagingKey = await StorageKeyOfAsync(database.GetConnectionString(), secondFileId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                null,
                [new OrderPaymentProofRequest(firstFileId, 10_000m), new OrderPaymentProofRequest(secondFileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Matches("^payment-proofs/[0-9a-f]{32}\\.webp$", Assert.Single(factory.PublicObjectStorage.DeletedKeys));
        Assert.Empty(factory.PublicObjectStorage.Copies);
        Assert.True(factory.ObjectStorage.Exists(firstStagingKey));
        Assert.True(factory.ObjectStorage.Exists(secondStagingKey));
    }

    // D13: un comprobante User se copia como en v1, pero Storage no lo mueve: el original sigue en el
    // bucket privado y el archivo no gana clave pública.
    [Fact]
    public async Task AUserProofIsCopiedButNeverMoved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var privateKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);

        var order = await ConvertAsync(client, tenantId, quotation.Id, "FullPaymentReceived", fileId);

        Assert.Matches(PublicKeyPattern, Assert.Single(await PublicKeysAsync(factory, order.Id)));
        Assert.True(await WaitForMoveInboxAsync(database.GetConnectionString()));
        Assert.Null(await PublicStorageKeyOfAsync(database.GetConnectionString(), fileId));
        Assert.True(factory.ObjectStorage.Exists(privateKey));
    }

    // D16 (spec 2026-09-16): un comprobante ya movido no se adjunta a otro pedido. Su temporal ya no
    // existe; en R2 la copia fallaría con 500. Se rechaza antes de copiar, con el código de siempre.
    [Fact]
    public async Task AnAlreadyMovedPaymentProofCannotBeAttachedToAnotherOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var firstQuotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, firstQuotation.Id, "FullPaymentReceived", fileId);
        Assert.NotNull(await WaitForMovedKeyAsync(database.GetConnectionString(), fileId));
        var secondQuotation = await NewSentQuotationAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, secondQuotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.payment_proof.file_not_available", problem?.Code);
        // Sólo la copia de la primera conversión: la segunda no copió nada.
        Assert.Single(factory.PublicObjectStorage.Copies);
    }

    // Revisión final (I2): un PaymentProof, un adjunto (D16), por referencia y no por movimiento. Con la
    // opción apagada el comprobante nunca se mueve, y antes podía adjuntarse a cualquier cantidad de
    // pedidos.
    [Fact]
    public async Task APaymentProofAlreadyAttachedButNotMovedCannotBeAttachedToAnotherOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var firstQuotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, firstQuotation.Id, "FullPaymentReceived", fileId);
        var secondQuotation = await NewSentQuotationAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, secondQuotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.payment_proof.file_not_available", problem?.Code);
    }

    // Revisión final (I2): tampoco como reemplazo de otro comprobante del mismo pedido.
    [Fact]
    public async Task APaymentProofOfAnotherProofCannotBeUsedAsAReplacement()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var firstFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var order = await ConvertAsync(
            client, tenantId, quotation.Id, "PartialPaymentReceived", firstFileId, secondFileId);
        var firstProofId = order.PaymentProofs.Single(proof => proof.FileId == firstFileId).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(firstProofId, 20_000m, secondFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.payment_proof.file_not_available", problem?.Code);
    }

    // Revisión final (I2): la excepción es el comprobante que se reemplaza con su propio archivo, que
    // todavía no se movió.
    [Fact]
    public async Task APaymentProofCanBeReplacedWithItsOwnFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, fileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Revisión final (I2): un archivo User (D13) sigue como en v1: se puede adjuntar a otro pedido.
    [Fact]
    public async Task AUserFileCanStillBeAttachedToAnotherOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var firstQuotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        await ConvertAsync(client, tenantId, firstQuotation.Id, "FullPaymentReceived", fileId);
        var secondQuotation = await NewSentQuotationAsync(client, factory, tenantId);

        var order = await ConvertAsync(client, tenantId, secondQuotation.Id, "FullPaymentReceived", fileId);

        Assert.Equal(fileId, Assert.Single(order.PaymentProofs).FileId);
    }

    // Revisión final (I2): el mismo archivo dos veces en un request se rechaza antes de copiar nada.
    [Fact]
    public async Task ConvertingWithTheSameFileTwiceIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived",
                null,
                [new OrderPaymentProofRequest(fileId, 10_000m), new OrderPaymentProofRequest(fileId, 10_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Empty(factory.PublicObjectStorage.Copies);
    }

    // Revisión final (I2): tampoco como comprobante nuevo y como reemplazo a la vez.
    [Fact]
    public async Task AddingTheSameFileAsANewProofAndAsAReplacementIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var oldFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", oldFileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var copiesBefore = factory.PublicObjectStorage.Copies.Count;
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [new OrderPaymentProofRequest(fileId, 10_000m)],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, fileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.Equal(copiesBefore, factory.PublicObjectStorage.Copies.Count);
    }

    // Revisión final (I2): corregir el mismo comprobante dos veces en un request se rechaza.
    [Fact]
    public async Task UpdatingTheSameProofTwiceIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs:
                [
                    new OrderPaymentProofUpdateRequest(proofId, 15_000m),
                    new OrderPaymentProofUpdateRequest(proofId, 20_000m),
                ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
    }

    // D19 (spec 2026-09-16): reemplazar el archivo de un comprobante escribe, con el pedido, el archivo
    // viejo y la clave de su copia.
    [Fact]
    public async Task ReplacingAProofFileWritesTheDetachedEventWithTheOldFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var oldFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", oldFileId);
        var oldKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, newFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, DetachedEventName));
        var payload = JsonSerializer.Deserialize<DetachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        Assert.Equal(tenantId, payload.TenantId);
        Assert.Equal(order.Id, payload.OrderId);
        var detached = Assert.Single(payload.Proofs);
        Assert.Equal(oldFileId, detached.FileId);
        Assert.Equal(oldKey, detached.PublicStorageKey);
    }

    // D19: reemplazar un comprobante User por su mismo archivo hace una copia nueva, con otra clave; la
    // vieja se suelta aunque el pedido siga usando el archivo, y Storage la borra.
    [Fact]
    public async Task ReplacingAUserProofWithTheSameFileDetachesTheOldKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var oldKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        Assert.NotNull(oldKey);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, fileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        Assert.NotEqual(oldKey, newKey);
        var message = Assert.Single(await OutboxMessagesAsync(factory, DetachedEventName));
        var payload = JsonSerializer.Deserialize<DetachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        var detached = Assert.Single(payload.Proofs);
        Assert.Equal(fileId, detached.FileId);
        Assert.Equal(oldKey, detached.PublicStorageKey);
        Assert.True(await WaitForDeletedKeyAsync(factory, oldKey));
        Assert.Equal(newKey, Assert.Single(factory.PublicObjectStorage.Copies).Key);
    }

    // D19: quitar un comprobante también.
    [Fact]
    public async Task RemovingAProofWritesTheDetachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var publicKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, order.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, DetachedEventName));
        var payload = JsonSerializer.Deserialize<DetachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        Assert.Equal(order.Id, payload.OrderId);
        var detached = Assert.Single(payload.Proofs);
        Assert.Equal(fileId, detached.FileId);
        Assert.Equal(publicKey, detached.PublicStorageKey);
    }

    // D19 con la opción apagada: el evento sale igual, sin clave, para que Storage borre el temporal.
    [Fact]
    public async Task RemovingAProofWithPublicLinksOffWritesTheDetachedEventWithoutKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, order.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, DetachedEventName));
        var payload = JsonSerializer.Deserialize<DetachedEventPayload>(message.PayloadJson, Json);
        Assert.NotNull(payload);
        var detached = Assert.Single(payload.Proofs);
        Assert.Equal(fileId, detached.FileId);
        Assert.Null(detached.PublicStorageKey);
        // El null va escrito, no omitido: PaymentProofDetachProcessor lee la propiedad con GetProperty y
        // un campo que falta descarta el mensaje entero. Se lee del JSON crudo porque el record de arriba
        // deserializa igual un null y un campo ausente; jsonb reformatea espacios, así que no se compara
        // el texto.
        using var raw = JsonDocument.Parse(message.PayloadJson);
        var rawProof = Assert.Single(raw.RootElement.GetProperty("proofs").EnumerateArray());
        Assert.True(rawProof.TryGetProperty("publicStorageKey", out var rawKey));
        Assert.Equal(JsonValueKind.Null, rawKey.ValueKind);
    }

    // Corregir sólo un monto no suelta ningún archivo. Ya pasa antes de esta tarea.
    [Fact]
    public async Task CorrectingOnlyAmountsWritesNoDetachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived", [], UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, DetachedEventName));
    }

    // D19, «misma transacción»: un retiro que el dominio rechaza no deja evento. Ya pasa antes de esta
    // tarea.
    [Fact]
    public async Task ARejectedRemovalWritesNoDetachedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, order.Id)}/{Guid.CreateVersion7()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken);
        Assert.Equal("order.payment_proof.not_found", problem?.Code);
        Assert.Empty(await OutboxMessagesAsync(factory, DetachedEventName));
    }

    // D19 de punta a punta: reemplazar el archivo de un comprobante movido borra su copia pública —la
    // única que tenía— y lo deja Purged. El reemplazo se mueve como cualquier comprobante nuevo.
    [Fact]
    public async Task AReplacedPaymentProofImageIsPurgedWithItsPublicCopy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var oldFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", oldFileId);
        var oldKey = await WaitForMovedKeyAsync(database.GetConnectionString(), oldFileId);
        Assert.NotNull(oldKey);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, order.Id),
            new AddOrderPaymentProofsRequest(
                "FullPaymentReceived",
                [],
                UpdatedProofs: [new OrderPaymentProofUpdateRequest(proofId, 20_000m, newFileId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Purged", await WaitForStatusAsync(database.GetConnectionString(), oldFileId, "Purged"));
        Assert.Contains(oldKey, factory.PublicObjectStorage.DeletedKeys);
        // Sólo queda la copia del reemplazo.
        Assert.Single(factory.PublicObjectStorage.Copies);
        Assert.NotNull(await WaitForMovedKeyAsync(database.GetConnectionString(), newFileId));
    }

    // D19 de punta a punta: quitar un comprobante movido, lo mismo.
    [Fact]
    public async Task ARemovedPaymentProofImageIsPurgedWithItsPublicCopy()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofImageAsync(client, factory, tenantId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var publicKey = await WaitForMovedKeyAsync(database.GetConnectionString(), fileId);
        Assert.NotNull(publicKey);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, order.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Purged", await WaitForStatusAsync(database.GetConnectionString(), fileId, "Purged"));
        Assert.Contains(publicKey, factory.PublicObjectStorage.DeletedKeys);
        Assert.Empty(factory.PublicObjectStorage.Copies);
    }

    // D13 y D19: un comprobante User conserva su original privado, pero la copia pública de ese adjunto
    // se borra al quitarlo. Antes de D19, quitar un comprobante no borraba nada.
    [Fact]
    public async Task ARemovedUserProofLosesItsPublicCopyAndKeepsItsOriginal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), publicPaymentProofLinks: true);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var quotation = await NewSentQuotationAsync(client, factory, tenantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var privateKey = await StorageKeyOfAsync(database.GetConnectionString(), fileId);
        var order = await ConvertAsync(client, tenantId, quotation.Id, "PartialPaymentReceived", fileId);
        var publicKey = Assert.Single(await PublicKeysAsync(factory, order.Id));
        Assert.NotNull(publicKey);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var response = await client.DeleteAsync(
            $"{OrderProofsUrl(tenantId, order.Id)}/{proofId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await WaitForDeletedKeyAsync(factory, publicKey));
        Assert.Equal("Available", await StatusOfAsync(database.GetConnectionString(), fileId));
        Assert.True(factory.ObjectStorage.Exists(privateKey));
    }

    private static async Task<string?> StatusOfAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT status FROM storage.file_resources WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }

    // El retiro lo corre PaymentProofMoveWorker cada 3 s en el host: se espera con plazo, como
    // WaitForMovedKeyAsync. Devuelve el último estado leído.
    private static async Task<string?> WaitForStatusAsync(string connectionString, Guid fileId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string? status = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            status = await StatusOfAsync(connectionString, fileId);
            if (status == expected)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return status;
    }

    private static async Task<bool> WaitForDeletedKeyAsync(QepApiFactory factory, string publicKey)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (factory.PublicObjectStorage.DeletedKeys.Contains(publicKey))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task AssertMovedAsync(
        string connectionString, QepApiFactory factory, Guid orderId, Guid fileId, string stagingKey)
    {
        var publicKey = Assert.Single(await PublicKeysAsync(factory, orderId));
        Assert.NotNull(publicKey);
        Assert.Matches("^payment-proofs/[0-9a-f]{32}\\.webp$", publicKey);
        Assert.Equal(stagingKey, factory.PublicObjectStorage.Copies[publicKey]);
        Assert.Equal(publicKey, await WaitForMovedKeyAsync(connectionString, fileId));
        Assert.False(factory.ObjectStorage.Exists(stagingKey));
    }

    private static async Task<string> StorageKeyOfAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT storage_key FROM storage.file_resources WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string?> PublicStorageKeyOfAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT public_storage_key FROM storage.file_resources WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }

    // PaymentProofMoveWorker corre solo en el host de pruebas, cada 3 s: se espera con plazo, igual
    // que WaitForEmailStatusAsync.
    private static async Task<string?> WaitForMovedKeyAsync(string connectionString, Guid fileId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await PublicStorageKeyOfAsync(connectionString, fileId) is { } key)
            {
                return key;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private static async Task<bool> WaitForMoveInboxAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM storage.inbox_messages WHERE consumer = 'storage.payment-proof-move'",
                connection);
            var count = Convert.ToInt64(
                await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                CultureInfo.InvariantCulture);
            if (count > 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<QuotationResponse> NewSentQuotationAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        return await CreateSentQuotationAsync(client, factory, tenantId, customerId, productId);
    }

    private static async Task<OrderResponse> ConvertAsync(
        HttpClient client, Guid tenantId, Guid quotationId, string paymentStatus, params Guid[] proofFileIds)
    {
        var response = await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotationId),
            new ConvertQuotationToOrderRequest(
                paymentStatus,
                null,
                proofFileIds.Select(fileId => new OrderPaymentProofRequest(fileId, 10_000m)).ToArray()),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    // La respuesta no expone la clave: se lee de la base.
    private static async Task<string?[]> PublicKeysAsync(QepApiFactory factory, Guid orderId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var id = new OrderId(orderId);
        return await dbContext.OrderPaymentProofs
            .AsNoTracking()
            .Where(proof => proof.OrderId == id)
            .Select(proof => proof.PublicStorageKey)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private sealed record ProblemDto(string? Code);

    private sealed record AttachedEventPayload(Guid TenantId, Guid OrderId, IReadOnlyList<AttachedEventProof> Proofs);

    private sealed record AttachedEventProof(Guid FileId, string PublicStorageKey);

    private sealed record DetachedEventPayload(Guid TenantId, Guid OrderId, IReadOnlyList<DetachedEventProof> Proofs);

    private sealed record DetachedEventProof(Guid FileId, string? PublicStorageKey);
}
