using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Lo que comparten las acciones del ticket: guardar y auditar sólo si algo cambió. Quién actúa, el ticket y la versión los
/// resuelve <see cref="SupportTicketScope"/> (spec 2026-11-22 §5.6).</summary>
internal static class SupportTicketActions
{
    /// <summary>Si la acción cambió algo (subió la versión), audita y guarda en la misma transacción; un no-op no escribe nada.</summary>
    public static async Task SaveIfChangedAsync(
        SupportTicket ticket,
        long versionBefore,
        IExecutionContext executionContext,
        ISupportAuditPublisher auditPublisher,
        ISupportUnitOfWork unitOfWork,
        string action,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (ticket.Version == versionBefore)
        {
            return;
        }

        auditPublisher.Publish(
            ticket.TenantId,
            executionContext.SubjectId,
            action,
            SupportAuditActions.Resources.Ticket,
            ticket.Id.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
