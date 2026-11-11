using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Decisión del dueño de producto (2026-10-05): los comprobantes se comparan contra el neto a cobrar
/// (<see cref="Quotation.NetTotal"/>), no contra lo facturado (<see cref="Quotation.Total"/>). Con
/// retención en la fuente el cliente no paga en efectivo lo que retiene, así que exigirle el total bruto
/// dejaba en "pago parcial" a quien pagó todo lo que debía. Desde spec 2026-11-10 §3.2 cubrir el neto
/// deja el pedido «Por verificar», no pagado. Quitar un comprobante es el caso de uso más chico que
/// recalcula el estado de pago desde el servidor; el resto (agregar, editar o quitar productos, guardar o
/// previsualizar la edición) comparte el mismo <see cref="Order.RecalculatePaymentStatus"/>.
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
    [InlineData(true, 116_500, OrderPaymentStatus.PendingVerification)]
    [InlineData(true, 116_499, OrderPaymentStatus.PartialPaymentReceived)]
    [InlineData(false, 119_000, OrderPaymentStatus.PendingVerification)]
    [InlineData(false, 118_999, OrderPaymentStatus.PartialPaymentReceived)]
    public async Task RemovingAProofComparesWhatRemainsAgainstTheNetAmountDue(
        bool customerWithRetention, decimal remainingProofs, OrderPaymentStatus expected)
    {
        var quotation = NewQuotationWithOneItem(customerWithRetention);
        Assert.Equal(119_000m, quotation.Total);
        Assert.Equal(customerWithRetention ? 116_500m : 119_000m, quotation.NetTotal);

        var order = Order.FromQuotation(
            OrderId.New(),
            TenantId,
            "PED-2026-0001",
            quotation,
            notes: null,
            AdvisorId,
            [
                new OrderPaymentProofInput(Guid.CreateVersion7(), remainingProofs),
                new OrderPaymentProofInput(Guid.CreateVersion7(), 5_000m),
            ],
            Now);
        var mistaken = order.PaymentProofs.Single(proof => proof.Amount == 5_000m);

        var handler = NewHandler(new StubOrderListRepository(order));

        var result = await handler.HandleAsync(
            new RemoveOrderPaymentProofCommand(TenantId, order.Id.Value, mistaken.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, order.PaymentStatus);
        Assert.Equal(expected.ToString(), result.PaymentStatus);
    }

    // Spec 2026-11-10 §3.2: quitar un comprobante que dejaba el neto cubierto baja el pago, y el historial lo
    // dice con el texto del sistema, no sólo con «Comprobantes: 1 retirado(s)».
    [Fact]
    public async Task RemovingAProofThatChangesThePaymentLeavesItInTheHistory()
    {
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now,
            proofs:
            [
                new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m),
                new OrderPaymentProofInput(Guid.CreateVersion7(), 19_000m),
            ]);
        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        var repository = new StubOrderListRepository(order);

        await NewHandler(repository).HandleAsync(
            new RemoveOrderPaymentProofCommand(
                TenantId, order.Id.Value, order.PaymentProofs.Single(proof => proof.Amount == 19_000m).Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            repository.History,
            entry => entry.EventType == OrderHistoryEventType.PaymentStatusRecalculated
                && entry.Details == "El pago pasó de Por verificar a Pago parcial: el neto pasó de 119.000 a 119.000.");
    }

    // Spec 2026-11-10 §3.2: ningún camino con sólo `manage` deshace el pagado. Quitar un comprobante de un
    // pedido pagado exige `approve`, y el 403 llega antes de tocar el pedido.
    [Fact]
    public async Task RemovingAProofFromAPaidOrderNeedsApprove()
    {
        var order = PaidOrderWithTwoProofs();
        var proofId = order.PaymentProofs.First().Id;

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            NewHandler(
                new StubOrderListRepository(order),
                new GrantedPermissionsExecutionContext(SubjectId, TenantId, "quotations.order.manage"))
            .HandleAsync(
                new RemoveOrderPaymentProofCommand(TenantId, order.Id.Value, proofId.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, order.PaymentProofs.Count);
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
    }

    [Fact]
    public async Task RemovingAProofFromAPaidOrderWithApproveKeepsItPaid()
    {
        var order = PaidOrderWithTwoProofs();
        var proofId = order.PaymentProofs.First().Id;

        var result = await NewHandler(
                new StubOrderListRepository(order),
                new GrantedPermissionsExecutionContext(
                    SubjectId, TenantId, "quotations.order.manage", "quotations.order.approve"))
            .HandleAsync(
                new RemoveOrderPaymentProofCommand(TenantId, order.Id.Value, proofId.Value),
                TestContext.Current.CancellationToken);

        Assert.Single(order.PaymentProofs);
        Assert.Equal("FullPaymentReceived", result.PaymentStatus);
    }

    // Un pedido sin pagar sigue con el permiso de gestión de siempre.
    [Fact]
    public async Task RemovingAProofFromAnUnpaidOrderNeedsOnlyManage()
    {
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 1_000m)]);

        await NewHandler(
                new StubOrderListRepository(order),
                new GrantedPermissionsExecutionContext(SubjectId, TenantId, "quotations.order.manage"))
            .HandleAsync(
                new RemoveOrderPaymentProofCommand(TenantId, order.Id.Value, order.PaymentProofs.Single().Id.Value),
                TestContext.Current.CancellationToken);

        Assert.Empty(order.PaymentProofs);
    }

    private static Order PaidOrderWithTwoProofs()
    {
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now,
            proofs:
            [
                new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m),
                new OrderPaymentProofInput(Guid.CreateVersion7(), 19_000m),
            ]);
        order.MarkPaid(AdvisorId, requirePaymentProof: false, Now);
        return order;
    }

    private static RemoveOrderPaymentProofHandler NewHandler(
        StubOrderListRepository repository, IExecutionContext? executionContext = null) =>
        new(
            repository,
            new NoOpQuotationsUnitOfWork(),
            new NoOpQuotationAuditPublisher(),
            new NoOpOrderPaymentProofEventPublisher(),
            new StubMembershipDirectory(AdvisorId.Value),
            new StubTenantDirectory(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now.AddHours(1)));

    private sealed class GrantedPermissionsExecutionContext(
        Guid subjectId, Guid tenantId, params string[] grantedPermissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => grantedPermissions.Contains(permission);
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
            OrderBuilders.BillingAccount,
            defaultCurrency: "COP",
            customerWithRetention,
            customerVatSurplus: false,
            AdvisorId,
            Now);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
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
