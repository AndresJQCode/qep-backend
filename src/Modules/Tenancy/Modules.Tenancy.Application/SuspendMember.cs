using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record SuspendMemberCommand(
    TenantId TenantId,
    MembershipId MembershipId,
    string CorrelationId) : ICommand<MembershipListItemDto>;

public sealed class SuspendMemberHandler(
    IMembershipRepository membershipRepository,
    IUserDirectory userDirectory,
    IRolePermissionChecker rolePermissionChecker,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock)
    : ICommandHandler<SuspendMemberCommand, MembershipListItemDto>
{
    public async Task<MembershipListItemDto> HandleAsync(
        SuspendMemberCommand command,
        CancellationToken cancellationToken)
    {
        EnsureAuthorized(command.TenantId);

        var membership = await MembershipLoader.LoadAsync(
            membershipRepository, command.MembershipId, command.TenantId, cancellationToken);

        if (membership.UserId == executionContext.SubjectId)
        {
            throw new TenantDomainException(
                "tenancy.membership.cannot_target_self",
                "A member cannot suspend their own membership.");
        }

        if (rolePermissionChecker.AnyGrants(membership.Roles, TenancyPermissions.AdvisorshipManage))
        {
            var others = await membershipRepository.ListActiveExcludingAsync(
                command.TenantId, command.MembershipId, cancellationToken);
            var hasOtherManager = others.Any(
                other => rolePermissionChecker.AnyGrants(
                    other.Roles, TenancyPermissions.AdvisorshipManage));
            if (!hasOtherManager)
            {
                throw new TenantDomainException(
                    "tenancy.membership.last_active_manager",
                    "The tenant must retain at least one member who can manage memberships.");
            }
        }

        var now = clock.UtcNow;
        membership.Suspend(now);

        auditRecorder.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            "tenancy.membership.suspended",
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
