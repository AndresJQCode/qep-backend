using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Los pasos que comparten las acciones del pedido de spec 2026-11-10 (marcar pagado, preparar, archivar y
/// sus deshacer), en el orden de <see cref="InvoiceOrderHandler"/>: permiso, pedido (403 de otro tenant, 404
/// del propio), membresía de quien actúa; y después del dominio, historial y auditoría en la misma unidad
/// de trabajo.
/// </summary>
internal static class OrderStateAction
{
    public static async Task<(Order Order, MemberId Actor)> LoadAsync(
        IOrderRepository repository,
        IMembershipDirectory membershipDirectory,
        IExecutionContext executionContext,
        Guid tenantId,
        Guid orderId,
        string permission,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, tenantId, permission);
        var order = await repository.FindByIdAsync(tenantId, new OrderId(orderId), cancellationToken)
            ?? throw OrderNotFound.ById(orderId);
        // Las marcas guardan un id de membresía, como CancelledBy: el módulo nunca guarda el usuario.
        var actor = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, tenantId, cancellationToken);
        return (order, actor);
    }

    public static void Record(
        IOrderRepository repository,
        IQuotationAuditPublisher auditPublisher,
        IExecutionContext executionContext,
        Guid tenantId,
        Order order,
        MemberId actor,
        OrderHistoryEventType eventType,
        string details,
        string auditAction,
        DateTimeOffset now)
    {
        repository.AddHistoryEntry(OrderHistoryEntry.Create(
            OrderHistoryEntryId.New(), order.Id, eventType, actor, details, now));
        auditPublisher.Publish(tenantId, executionContext.SubjectId, auditAction, order.Id.ToString(), "success", now);
    }
}
