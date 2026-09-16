using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Quita un comprobante ya cargado (a pedido, 2026-09-15) — para el caso de haber cargado uno
/// equivocado, no para corregirlo (eso ya lo cubre <see cref="AddOrderPaymentProofsCommand"/> vía
/// <c>UpdatedProofs</c>). Sólo mientras el pedido sigue <see cref="OrderStatus.Pending"/> — ver
/// <see cref="Order.RemovePaymentProof"/>. Desde D19 (spec 2026-09-16) el archivo que el pedido deja de
/// usar se borra: lo hace Storage, al consumir el evento que se escribe con el pedido.
/// </summary>
public sealed record RemoveOrderPaymentProofCommand(
    Guid TenantId, Guid QuotationId, Guid ProofId) : ICommand<OrderDto>;

public sealed class RemoveOrderPaymentProofHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
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

        var order = await orderRepository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        var quotation = await quotationRepository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var now = clock.UtcNow;
        var proofId = new OrderPaymentProofId(command.ProofId);
        // D19: el archivo y su clave, leídos antes de quitarlo. Si el comprobante no es de este pedido,
        // RemovePaymentProof lanza order.payment_proof.not_found y no se publica nada.
        var removed = order.PaymentProofs.FirstOrDefault(proof => proof.Id == proofId);
        order.RemovePaymentProof(proofId, now);
        // El estado del pago cambia con lo que quede cargado -- mismo motivo que
        // AddOrderItemsHandler recalcula tras sumar un producto: el agregado no tiene el total de
        // la cotización a mano.
        order.RecalculatePaymentStatus(quotation.Total, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.payment_proof_removed",
            order.Id.ToString(),
            "success",
            now);
        // D19: en la misma transacción que el pedido. `removed` no es null: RemovePaymentProof ya habría
        // lanzado. Si otro comprobante del pedido usa el mismo archivo, no se suelta.
        var detached = PaymentProofCopies.DetachedFrom(
            [new DetachedPaymentProof(removed!.FileId, removed.PublicStorageKey)],
            order.PaymentProofs.Select(proof => proof.FileId));
        if (detached.Length > 0)
        {
            paymentProofEvents.PublishDetached(command.TenantId, order.Id, detached, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
