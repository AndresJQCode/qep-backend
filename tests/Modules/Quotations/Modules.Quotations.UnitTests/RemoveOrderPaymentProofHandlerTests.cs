using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Decisión del dueño de producto (2026-10-05): un pedido queda pagado cuando los comprobantes
/// cubren el neto a cobrar (<see cref="Quotation.NetTotal"/>), no lo facturado
/// (<see cref="Quotation.Total"/>). Con retención en la fuente el cliente no paga en efectivo lo
/// que retiene, así que exigirle el total bruto dejaba en "pago parcial" a quien pagó todo lo que
/// debía. Quitar un comprobante es el caso de uso más chico que recalcula el estado de pago desde
/// el servidor; el resto (agregar, editar o quitar productos, guardar o previsualizar la edición)
/// comparte el mismo <see cref="Order.RecalculatePaymentStatus"/> y lo cubren las pruebas de
/// integración.
/// </summary>
public sealed class RemoveOrderPaymentProofHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);

    // 119_000 con el IVA del 19% adentro: base 100_000, total 119_000. Con retención, el 2.5% de la
    // base (2_500) sale del neto: 116_500.
    [Theory]
    [InlineData(true, 116_500, OrderPaymentStatus.FullPaymentReceived)]
    [InlineData(true, 116_499, OrderPaymentStatus.PartialPaymentReceived)]
    [InlineData(false, 119_000, OrderPaymentStatus.FullPaymentReceived)]
    [InlineData(false, 118_999, OrderPaymentStatus.PartialPaymentReceived)]
    public async Task RemovingAProofComparesWhatRemainsAgainstTheNetAmountDue(
        bool customerWithRetention, decimal remainingProofs, OrderPaymentStatus expected)
    {
        var quotation = NewQuotationWithOneItem(customerWithRetention);
        Assert.Equal(119_000m, quotation.Total);
        Assert.Equal(customerWithRetention ? 116_500m : 119_000m, quotation.NetTotal);

        var order = Order.Create(
            OrderId.New(),
            TenantId,
            "PED-2026-0001",
            quotation.Id,
            OrderPaymentStatus.PartialPaymentReceived,
            notes: null,
            AdvisorId,
            [
                new OrderPaymentProofInput(Guid.CreateVersion7(), remainingProofs),
                new OrderPaymentProofInput(Guid.CreateVersion7(), 5_000m),
            ],
            Now);
        var mistaken = order.PaymentProofs.Single(proof => proof.Amount == 5_000m);

        var handler = new RemoveOrderPaymentProofHandler(
            new StubOrderListRepository(new OrderWithQuotation(order, quotation)),
            new StubQuotationRepository(quotation),
            new NoOpQuotationsUnitOfWork(),
            new NoOpQuotationAuditPublisher(),
            new NoOpOrderPaymentProofEventPublisher(),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now.AddHours(1)));

        var result = await handler.HandleAsync(
            new RemoveOrderPaymentProofCommand(TenantId, order.Id.Value, mistaken.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, order.PaymentStatus);
        Assert.Equal(expected.ToString(), result.PaymentStatus);
    }

    private static Quotation NewQuotationWithOneItem(bool customerWithRetention)
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            Guid.CreateVersion7(),
            AdvisorId,
            new DateOnly(2026, 10, 30),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            defaultCurrency: QuotationCurrency.Cop,
            customerWithRetention,
            customerVatSurplus: false,
            AdvisorId,
            Now);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        return quotation;
    }

    private sealed class NoOpOrderPaymentProofEventPublisher : IOrderPaymentProofEventPublisher
    {
        public void PublishAttached(
            Guid tenantId, OrderId orderId, IReadOnlyCollection<AttachedPaymentProof> proofs,
            DateTimeOffset occurredAt)
        {
        }

        public void PublishDetached(
            Guid tenantId, OrderId orderId, IReadOnlyCollection<DetachedPaymentProof> proofs,
            DateTimeOffset occurredAt)
        {
        }
    }
}
