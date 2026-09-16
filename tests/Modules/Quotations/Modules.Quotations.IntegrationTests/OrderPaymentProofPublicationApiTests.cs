using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string OrderUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    private static string OrderProofsUrl(Guid tenantId, Guid quotationId) =>
        $"{OrderUrl(tenantId, quotationId)}/proofs";

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
            OrderProofsUrl(tenantId, quotation.Id),
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
            OrderProofsUrl(tenantId, quotation.Id),
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
            OrderProofsUrl(tenantId, quotation.Id),
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
        await ConvertAsync(client, tenantId, quotation.Id, "PaymentPending");
        var firstFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        factory.PublicObjectStorage.FailingCopyAttempt = 2;

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
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
            $"{OrderUrl(tenantId, quotation.Id)}/approve", content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var secondFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            OrderProofsUrl(tenantId, quotation.Id),
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
            OrderProofsUrl(tenantId, quotation.Id),
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
            OrderProofsUrl(tenantId, quotation.Id),
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
            OrderProofsUrl(tenantId, quotation.Id),
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
}
