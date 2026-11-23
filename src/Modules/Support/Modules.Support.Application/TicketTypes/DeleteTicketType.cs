using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>BuildingBlocks no tiene un <c>ICommand</c> sin resultado: el endpoint responde 204 y no lo mira (precedente
/// <c>CancelUploadResult</c> de Storage).</summary>
public sealed record DeleteTicketTypeResult(Guid Id);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record DeleteTicketTypeCommand(Guid TenantId, Guid TypeId, long ExpectedVersion) : ICommand<DeleteTicketTypeResult>;

/// <summary>
/// Fase 4a (spec §12, plan G4 y G5): borra un tipo que ningún ticket usa, en ningún estado; uno usado se desactiva con el <c>PUT</c>.
/// Orden: autorización → provisión → tipo (404) → versión (412) → pregunta previa (422 <c>support.ticket_type.in_use</c>) → borrado y
/// auditoría → guardar. La pregunta da la respuesta amable; la regla la cierra la FK <c>RESTRICT</c> de <c>tickets.ticket_type_id</c>,
/// que <c>SupportUnitOfWork</c> traduce por nombre: si un alta gana la carrera entre la pregunta y el guardado, el borrado sale con el
/// mismo 422. No avisa por el hub: no cambia ningún ticket.
/// </summary>
public sealed class DeleteTicketTypeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportTicketRepository tickets,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher)
    : ICommandHandler<DeleteTicketTypeCommand, DeleteTicketTypeResult>
{
    private static readonly string[] DeletedFields = ["id"];

    public async Task<DeleteTicketTypeResult> HandleAsync(DeleteTicketTypeCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var type = await types.FindAsync(command.TenantId, new SupportTicketTypeId(command.TypeId), cancellationToken)
            ?? throw SupportNotFound.TicketType(command.TypeId);
        // El tipo interno lo siembra el módulo y no se borra (spec 2026-11-22, D11), aunque ningún ticket lo use todavía.
        if (type.IsInternal)
        {
            throw new SupportDomainException(SupportErrorCodes.InternalTypeLocked, "The internal ticket type cannot be deleted.");
        }

        if (type.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The ticket type changed after it was loaded.");
        }

        if (await tickets.AnyOfTypeAsync(command.TenantId, type.Id, cancellationToken))
        {
            throw new SupportDomainException(SupportErrorCodes.TicketTypeInUse, "The ticket type is used by at least one ticket.");
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        types.Remove(type);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.TicketTypeDeleted,
            SupportAuditActions.Resources.TicketType,
            type.Id.ToString(),
            "success",
            DeletedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DeleteTicketTypeResult(type.Id.Value);
    }
}
