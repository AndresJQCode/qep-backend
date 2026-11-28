using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El archivado automático (spec 2026-11-10 §3.5): con el ajuste del tenant prendido, el pedido se archiva
/// en el instante en que queda pagado y preparado, por la segunda de las dos acciones que llegue. El autor
/// es quien hizo esa acción. Recibe los ajustes ya leídos: quien llama los necesita también para su propia
/// guarda, y leerlos dos veces abriría una ventana entre lecturas.
/// </summary>
internal static class OrderAutoArchive
{
    public const string AuditAction = "quotation.order.archived";

    public static bool Apply(
        OrderSettings settings,
        IOrderRepository repository,
        IQuotationAuditPublisher auditPublisher,
        IQuotationLifecycleEventPublisher lifecycleEvents,
        IExecutionContext executionContext,
        Guid tenantId,
        Order order,
        MemberId actor,
        DateTimeOffset now)
    {
        if (!settings.AutoArchive || !order.ArchiveIfSettled(actor, now))
        {
            return false;
        }

        // Justo después de ArchiveIfSettled: la versión del evento es la que dejó el archivo, mayor que la del preparado o el
        // pago que lo precedió en el mismo caso de uso.
        lifecycleEvents.PublishOrderArchived(order, now);
        OrderStateAction.Record(
            repository, auditPublisher, executionContext, tenantId, order, actor,
            OrderHistoryEventType.Archived, OrderChangeSummary.ArchivedAutomatically(), AuditAction, now);
        return true;
    }
}
