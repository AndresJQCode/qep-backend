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
/// Returns a suspended membership to active.
/// </summary>
/// <remarks>
/// Deliberately its own use case rather than a side effect of inviting again (SDD-OD-13):
/// re-inviting and reinstating are different intentions, and if the first did the second an
/// administrator could undo somebody else's suspension while believing they were only
/// sending an invitation.
///
/// No `cannot_target_self` guard here, unlike Suspend and Remove. Those two protect against
/// locking yourself out; this one only restores access, and a subject who is suspended
/// cannot reach this endpoint anyway — their permission claims are resolved from an active
/// membership. No `last_active_manager` guard either: adding a manager never leaves the
/// tenant without one.
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
            !executionContext.HasPermission(TenancyPermissions.MembershipManage))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot manage memberships for this tenant.");
        }
    }
}
