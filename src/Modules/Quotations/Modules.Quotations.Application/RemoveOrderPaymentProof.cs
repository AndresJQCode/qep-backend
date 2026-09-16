using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Quita un comprobante ya cargado (a pedido, 2026-09-15) — para el caso de haber cargado uno
/// equivocado, no para corregirlo (eso ya lo cubre <see cref="AddOrderPaymentProofsCommand"/> vía
/// <c>UpdatedProofs</c>). Sólo mientras el pedido sigue <see cref="OrderStatus.Pending"/> — ver
/// <see cref="Order.RemovePaymentProof"/>.
/// </summary>
public sealed record RemoveOrderPaymentProofCommand(
    Guid TenantId, Guid QuotationId, Guid ProofId) : ICommand<OrderDto>;

public sealed class RemoveOrderPaymentProofHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
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
        order.RemovePaymentProof(new OrderPaymentProofId(command.ProofId), now);
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
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
