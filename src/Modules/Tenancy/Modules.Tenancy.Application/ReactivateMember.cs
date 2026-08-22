using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record ReactivateMemberCommand(
    TenantId TenantId,
    MembershipId MembershipId,
    string CorrelationId) : ICommand<MembershipListItemDto>;

/// <summary>
/// Devuelve una membresía suspendida a activa.
/// </summary>
/// <remarks>
/// Deliberadamente es su propio caso de uso y no un efecto lateral de volver a invitar
/// (SDD-OD-13): re-invitar y reinstaurar son intenciones distintas, y si lo primero hiciera
/// lo segundo un administrador podría deshacer la suspensión que puso otro creyendo que
/// sólo estaba mandando una invitación.
///
/// Acá no hay guarda `cannot_target_self`, a diferencia de Suspend y Remove. Esas dos
/// protegen contra dejarte afuera; ésta sólo restaura acceso, y un sujeto suspendido no
/// puede llegar a este endpoint de todos modos — sus claims de permiso se resuelven desde
/// una membresía activa. Tampoco hay guarda `last_active_manager`: agregar un manager nunca
/// deja al tenant sin ninguno.
/// </remarks>
public sealed class ReactivateMemberHandler(
    IMembershipRepository membershipRepository,
    IUserDirectory userDirectory,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock)
    : ICommandHandler<ReactivateMemberCommand, MembershipListItemDto>
{
    public async Task<MembershipListItemDto> HandleAsync(
        ReactivateMemberCommand command,
        CancellationToken cancellationToken)
    {
        EnsureAuthorized(command.TenantId);

        var membership = await MembershipLoader.LoadAsync(
            membershipRepository, command.MembershipId, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        membership.Reactivate(now);

        auditRecorder.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            "tenancy.membership.reactivated",
            "membership",
            membership.Id.ToString(),
            "success",
            [],
            now);

        foreach (var domainEvent in membership.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var email = await userDirectory.GetEmailAsync(membership.UserId, cancellationToken);
        return membership.ToListItemDto(email);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipManage))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot manage memberships for this tenant.");
        }
    }
}
