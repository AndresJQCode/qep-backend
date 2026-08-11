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

        // Aprovisiona (o resuelve) el usuario invitado por el contrato de Identity. Es
        // idempotente por email, así que re-invitar reutiliza el mismo id de usuario.
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
    /// Decide qué significa una segunda invitación para una membresía que ya existe.
    /// </summary>
    /// <remarks>
    /// La renovación pasa sobre la fila existente, nunca insertando una segunda:
    /// (UserId, TenantId) es UNIQUE (TenancyDbContext.cs:105). Crear acá una membresía nueva
    /// —como hacía este handler para cualquier estado que no fuera Invited/Active— viola ese
    /// índice y sale como un 500. Ver SDD-CT-15.
    /// </remarks>
    private async Task<MembershipDto> ReinviteExistingAsync(
        Membership existing,
        InviteMemberCommand command,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // Una invitación viva y una membresía activa son las dos no-ops. Renovar una invitación
        // viva movería un plazo con el que alguien cuenta e invalidaría el link que ya está en
        // su bandeja.
        var invitationIsLive =
            existing.State == MembershipState.Invited && now <= existing.ExpiresAt;
        if (invitationIsLive || existing.State == MembershipState.Active)
        {
            return existing.ToDto();
        }

        // Todo lo demás es o una invitación vencida (todavía en Invited, porque el vencimiento es
        // perezoso y nadie intentó entrar) o una ya marcada como Expired. Las dos son
        // renovables; Reinvite rechaza los estados que no lo son. SDD-OD-04.
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

        // La renovación sólo le llega a la persona por el outbox: InvitationDeliveryWorker
        // manda el email a partir de este evento. Persistir sin emitirlo no renueva nada
        // que el invitado pueda ver.
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
