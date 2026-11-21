using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Quita un regalo del pedido (spec 2026-11-21). El <c>ItemId</c> tiene que ser de un regalo: la línea pagada de la
/// misma variante no se quita por acá.</summary>
public sealed record RemoveOrderGiftCommand(Guid TenantId, Guid OrderId, Guid ItemId) : ICommand<OrderDetailDto>;

public sealed class RemoveOrderGiftHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveOrderGiftCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        RemoveOrderGiftCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);

        var order = await orderRepository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        var gift = order.Items.FirstOrDefault(item => item.IsGift && item.Id.Value == command.ItemId)
            ?? throw new QuotationsDomainException("order.item.not_found", "The order item was not found.");

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var name = gift.ProductName ?? gift.ProductCode ?? "un producto";

        order.RemoveItem(gift.Id, updatedBy, now);
        orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.ItemsChanged,
            updatedBy, OrderChangeSummary.GiftRemoved(name), now));
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "quotation.order.gift_removed",
            order.Id.ToString(), "success", now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await OrderDetails.LoadAsync(
            orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
