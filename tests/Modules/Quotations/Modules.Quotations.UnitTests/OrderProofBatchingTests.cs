using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Consultas por request: los comprobantes nuevos de un cuerpo se resuelven en una lectura a Storage y una de uso, no dos por
/// comprobante, en los tres casos de uso que los reciben (crear un pedido, guardar la edición y sumar comprobantes). Cada prueba
/// manda tres comprobantes y se detiene en el paso que sigue a resolverlos, que es buscar al asesor: lo de después no importa
/// acá, por eso las dependencias que no se alcanzan van en <c>null</c>.
/// </summary>
public sealed class OrderProofBatchingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QuotationFileRef[] ThreeProofs() =>
    [
        .. Enumerable.Range(0, 3).Select(_ => new QuotationFileRef(
            Guid.CreateVersion7(), TenantId, "application/pdf", 1024, IsAvailable: true, IsPaymentProof: true)),
    ];

    [Fact]
    public async Task CreateOrderResolvesThreeProofsInOneRead()
    {
        var files = ThreeProofs();
        var lookup = new CountingFileLookup(files);
        var orders = new StubOrderListRepository();
        var handler = new CreateOrderHandler(
            orderRepository: orders,
            quotationRepository: null!,
            unitOfWork: null!,
            auditPublisher: null!,
            drafts: null!,
            fileLookup: lookup,
            paymentProofPublisher: null!,
            paymentProofEvents: null!,
            lifecycleEvents: null!,
            numberGenerator: null!,
            numberingFormats: null!,
            membershipDirectory: new StopAtAdvisor(),
            executionContext: new StubExecutionContext(SubjectId, TenantId),
            tenantClock: null!,
            validator: new CreateOrderValidator());
        var command = new CreateOrderCommand(
            TenantId,
            Guid.CreateVersion7(),
            PaymentMethod: null,
            Notes: null,
            IsRetail: false,
            GlobalScaleFloor: null,
            Parties: null,
            new QuotationBillingAccountRequest(Guid.CreateVersion7(), "Bancolombia", "7542", "COP"),
            [new OrderItemAddition(Guid.CreateVersion7(), 1m)],
            [.. files.Select(file => new OrderPaymentProofRequest(file.FileId, 1_000m))]);

        await Assert.ThrowsAsync<AdvisorReachedException>(() => handler.HandleAsync(command, Ct));

        AssertTwoReads(lookup, orders);
    }

    [Fact]
    public async Task SaveOrderEditsResolvesThreeNewProofsInOneRead()
    {
        var files = ThreeProofs();
        var lookup = new CountingFileLookup(files);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now);
        var orders = new StubOrderListRepository(order);
        var handler = new SaveOrderEditsHandler(
            orderRepository: orders,
            quotationRepository: null!,
            unitOfWork: null!,
            auditPublisher: null!,
            pricingLookup: null!,
            settingsStore: null!,
            priceRounding: null!,
            productLookup: null!,
            fileLookup: lookup,
            paymentProofPublisher: null!,
            paymentProofEvents: null!,
            shippingResolver: null!,
            membershipDirectory: new StopAtAdvisor(),
            tenantDirectory: null!,
            executionContext: new StubExecutionContext(SubjectId, TenantId),
            clock: null!,
            validator: new SaveOrderEditsValidator(),
            companyLookup: null!,
            withholdingLookup: null!,
            tenantClock: null!,
            costLookup: null!,
            geographyLookup: null!,
            wholesalePricing: null!,
            customerLookup: null!,
            deliveryCheck: null!,
            lifecycleEvents: null!);
        var command = new SaveOrderEditsCommand(
            TenantId,
            order.Id.Value,
            order.Version,
            [new OrderItemAddition(Guid.CreateVersion7(), 1m)],
            new OrderEditProofs([.. files.Select(file => new OrderEditProofAddition(file.FileId, 1_000m))], [], []),
            Notes: null,
            GlobalScaleFloor: null,
            IsRetail: false);

        await Assert.ThrowsAsync<AdvisorReachedException>(() => handler.HandleAsync(command, Ct));

        AssertTwoReads(lookup, orders);
    }

    [Fact]
    public async Task AddOrderPaymentProofsResolvesThreeNewProofsInOneRead()
    {
        var files = ThreeProofs();
        var lookup = new CountingFileLookup(files);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now);
        var orders = new StubOrderListRepository(order);
        var handler = new AddOrderPaymentProofsHandler(
            repository: orders,
            unitOfWork: null!,
            auditPublisher: null!,
            fileLookup: lookup,
            paymentProofPublisher: null!,
            paymentProofEvents: null!,
            membershipDirectory: new StopAtAdvisor(),
            tenantDirectory: null!,
            executionContext: new StubExecutionContext(SubjectId, TenantId),
            clock: null!,
            validator: new AddOrderPaymentProofsValidator());
        var command = new AddOrderPaymentProofsCommand(
            TenantId,
            order.Id.Value,
            Notes: null,
            [.. files.Select(file => new OrderPaymentProofRequest(file.FileId, 1_000m))],
            []);

        await Assert.ThrowsAsync<AdvisorReachedException>(() => handler.HandleAsync(command, Ct));

        AssertTwoReads(lookup, orders);
    }

    private static void AssertTwoReads(CountingFileLookup lookup, StubOrderListRepository orders)
    {
        Assert.Equal(1, lookup.FindManyCalls);
        Assert.Equal(0, lookup.FindCalls);
        Assert.Equal(1, orders.PaymentProofFileBatches);
    }

    private sealed class AdvisorReachedException : Exception;

    /// <summary>El paso que sigue a resolver los comprobantes: la prueba termina acá.</summary>
    private sealed class StopAtAdvisor : IMembershipDirectory
    {
        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
            Guid userId, Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> FindActiveMembershipIdAsync(
            Guid userId, Guid tenantId, CancellationToken cancellationToken) => throw new AdvisorReachedException();

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(
            Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
