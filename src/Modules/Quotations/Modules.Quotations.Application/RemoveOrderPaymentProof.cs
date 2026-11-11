using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record RemoveOrderPaymentProofCommand(
    Guid TenantId, Guid OrderId, Guid ProofId) : ICommand<OrderDto>;

/// <summary>
/// Quitar un comprobante de un pedido pagado exige además <see cref="OrdersPermissions.OrderApprove"/>
/// (spec 2026-11-10 §3.2): ningún camino con sólo <c>manage</c> deshace el pagado. Las dos rutas que quitan
/// comprobantes la llaman antes de tocar el pedido: <see cref="RemoveOrderPaymentProofHandler"/> y
/// <see cref="SaveOrderEditsHandler"/> (con <c>proofs.removeIds</c>). Agregar o corregir sigue con
/// <c>manage</c> y no cambia el pago.
/// </summary>
internal static class PaidOrderProofRemoval
{
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, Order order)
    {
        if (order.PaymentStatus == OrderPaymentStatus.FullPaymentReceived)
        {
            QuotationsAuthorization.EnsureAuthorized(executionContext, tenantId, OrdersPermissions.OrderApprove);
        }
    }
}

public sealed class RemoveOrderPaymentProofHandler(
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IMembershipDirectory membershipDirectory,
    ITenantDirectory tenantDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveOrderPaymentProofCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        RemoveOrderPaymentProofCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);

        var order = await orderRepository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        PaidOrderProofRemoval.EnsureAuthorized(executionContext, command.TenantId, order);

        var now = clock.UtcNow;
        var previousNetTotal = order.NetTotal;
        var proofId = new OrderPaymentProofId(command.ProofId);
        var removed = order.PaymentProofs.FirstOrDefault(proof => proof.Id == proofId);
        order.RemovePaymentProof(proofId, now);
        // El neto es del pedido (spec 2026-10-15 §4.1): ya no hace falta la cotización para saber
        // cuánto se debe.
        var paymentChange = order.RecalculatePaymentStatus(previousNetTotal, now);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PaymentProofsChanged, updatedBy,
            OrderChangeSummary.PaymentProofsChanged(added: 0, corrected: 0, removed: 1), now));
        if (paymentChange is not null)
        {
            var format = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PaymentStatusRecalculated, updatedBy,
                OrderChangeSummary.PaymentStatusRecalculated(paymentChange, format), now));
        }
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.payment_proof_removed",
            order.Id.ToString(),
            "success",
            now);

        // Spec 2026-09-15: la copia pública del comprobante retirado se despublica, salvo que otro
        // comprobante del mismo pedido siga usando el archivo.
        var detached = PaymentProofCopies.DetachedFrom(
            [new DetachedPaymentProof(removed!.FileId, removed.PublicStorageKey)],
            order.PaymentProofs.Select(proof => new DetachedPaymentProof(proof.FileId, proof.PublicStorageKey)));
        if (detached.Length > 0)
        {
            paymentProofEvents.PublishDetached(command.TenantId, order.Id, detached, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return order.ToDto();
    }
}
