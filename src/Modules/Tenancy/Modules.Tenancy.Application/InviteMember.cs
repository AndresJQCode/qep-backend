using FluentValidation;
using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record InviteMemberCommand(
    TenantId TenantId,
    string Email,
    IReadOnlyCollection<string> Roles,
    string CorrelationId) : ICommand<MembershipDto>;

public sealed class InviteMemberValidator : AbstractValidator<InviteMemberCommand>
{
    public InviteMemberValidator()
    {
        RuleFor(command => command.Email).NotEmpty().MaximumLength(254);
        RuleFor(command => command.Roles).NotNull();
    }
}

public sealed class InviteMemberHandler(
    IIdentityProvisioning identityProvisioning,
    IMembershipRepository membershipRepository,
    IRoleReferenceValidator roleReferenceValidator,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    IValidator<InviteMemberCommand> validator)
    : ICommandHandler<InviteMemberCommand, MembershipDto>
{
    private const string Origin = "invitation";

    public async Task<MembershipDto> HandleAsync(
        InviteMemberCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);
        EnsureKnownRoles(command.Roles);

        // Provision (or resolve) the invited user through the Identity contract. This
        // is idempotent by email, so a re-invite reuses the same user id.
        var userId = await identityProvisioning.GetOrProvisionInvitedUserAsync(
            command.Email,
            cancellationToken);

        var existing = await membershipRepository.FindByUserAndTenantAsync(
            userId,
            command.TenantId,
            cancellationToken);
        if (existing is not null)
        {
            return await ReinviteExistingAsync(existing, command, cancellationToken);
        }

        var membership = Membership.Invite(
            MembershipId.New(),
            userId,
            command.TenantId,
            command.Roles,
            Origin,
            clock.UtcNow,
            Membership.DefaultInvitationTimeToLive);
        membershipRepository.Add(membership);

        auditRecorder.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            "tenancy.membership.invited",
            "membership",
            membership.Id.ToString(),
            "success",
            [],
            clock.UtcNow);

        foreach (var domainEvent in membership.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return membership.ToDto();
    }

    /// <summary>
    /// Decides what a second invitation means for a membership that already exists.
    /// </summary>
    /// <remarks>
    /// Renewal happens on the existing row, never by inserting a second one:
    /// (UserId, TenantId) is UNIQUE (TenancyDbContext.cs:105). Creating a new membership
    /// here — as this handler did for any state other than Invited/Active — violates that
    /// index and surfaces as a 500. See SDD-CT-15.
    /// </remarks>
    private async Task<MembershipDto> ReinviteExistingAsync(
        Membership existing,
        InviteMemberCommand command,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // A live invitation and an active membership are both no-ops. Renewing a live
        // invitation would move a deadline someone is counting on and invalidate the link
        // already in their inbox.
        var invitationIsLive =
            existing.State == MembershipState.Invited && now <= existing.ExpiresAt;
        if (invitationIsLive || existing.State == MembershipState.Active)
        {
            return existing.ToDto();
        }

        // Everything else is either a lapsed invitation (still Invited, because expiry is
        // lazy and nobody tried to sign in) or one already marked Expired. Both are
        // renewable; Reinvite refuses the states that are not. SDD-OD-04.
        existing.Reinvite(command.Roles, now, Membership.DefaultInvitationTimeToLive);

        auditRecorder.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            "tenancy.membership.invited",
            "membership",
            existing.Id.ToString(),
            "success",
            [],
            now);

        // The renewal only reaches the person through the outbox: InvitationDeliveryWorker
        // sends the email off this event. Persisting without emitting it renews nothing
        // that the invitee can see.
        foreach (var domainEvent in existing.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return existing.ToDto();
    }

    private void EnsureKnownRoles(IReadOnlyCollection<string> roles)
    {
        var normalizedRoles = roles
            .Select(role => role.Trim())
            .Where(role => role.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedRoles.Length == 0)
        {
            throw new TenantDomainException(
                "tenancy.membership.roles_required",
                "A membership requires at least one role.");
        }

        var unknownRole = normalizedRoles.FirstOrDefault(
            role => !roleReferenceValidator.IsKnownRole(role));
        if (unknownRole is not null)
        {
            throw new TenantDomainException(
                "tenancy.membership.role_unknown",
                $"The role '{unknownRole}' is not part of the authorization catalog.");
        }
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.MembershipInvite))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot invite members to this tenant.");
        }
    }
}
