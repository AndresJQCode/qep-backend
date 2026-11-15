using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Lo que comparten las cuatro acciones del ticket (estado, responsable, tipo y cliente): quién actúa, el ticket del tenant
/// y la versión que la pantalla cargó.</summary>
internal static class SupportTicketActions
{
    /// <summary>
    /// La membresía activa de quien actúa (sin ella, 403: la traza nombra membresías), el ticket acotado al tenant de la ruta (uno de
    /// otro tenant es 404, igual que uno que no existe) y la versión del <c>If-Match</c> (vieja, 412).
    /// </summary>
    public static async Task<(SupportTicket Ticket, Guid Actor)> LoadAsync(
        IExecutionContext executionContext,
        IMembershipDirectory membershipDirectory,
        ISupportTicketRepository tickets,
        Guid tenantId,
        Guid ticketId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        var ticket = await tickets.FindAsync(tenantId, new SupportTicketId(ticketId), cancellationToken)
            ?? throw SupportNotFound.Ticket(ticketId);
        if (ticket.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The ticket changed after it was loaded.");
        }

        return (ticket, actor);
    }

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
