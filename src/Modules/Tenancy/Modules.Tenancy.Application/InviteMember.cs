using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record InviteMemberCommand(
    TenantId TenantId,
    string Email,
    string DisplayName,
    int? AdvisorCode,
    IReadOnlyCollection<string> Roles,
    string CorrelationId) : ICommand<MembershipDto>;

public sealed class InviteMemberValidator : AbstractValidator<InviteMemberCommand>
{
    public InviteMemberValidator()
    {
        RuleFor(command => command.Email).NotEmpty().MaximumLength(254);
        // El dominio ya rechaza el nombre vacío con su propio código, pero ese 422 no trae el
        // mapa `errors` y el formulario no sabría qué input marcar. Éste sí.
        RuleFor(command => command.DisplayName)
            .NotEmpty()
            .MaximumLength(Membership.DisplayNameMaxLength);
        // Mismo criterio para el código de asesor (spec 2026-09-24, D2): el dominio da
        // `advisor_code_invalid`, el validador da `errors.AdvisorCode`. Opcional (D1).
        RuleFor(command => command.AdvisorCode)
            .GreaterThan(0)
            .When(command => command.AdvisorCode is not null);
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
        // Cualquier rol del catálogo del tenant es invitable, incluido admin y los roles
        // definidos por el tenant: la única validación de rol es que el catálogo lo conozca.
        await EnsureKnownRolesAsync(command.TenantId, command.Roles, cancellationToken);

        // Desde acá hasta el commit se corre serializado con el borrado de usuarios huérfanos
        // de Identity; ver ITenancyUnitOfWork.BeginUserLifecycleScopeAsync.
        await using var lifecycle = await unitOfWork.BeginUserLifecycleScopeAsync(
            command.Email,
            cancellationToken);

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
            var renewed = await ReinviteExistingAsync(existing, command, cancellationToken);
            await lifecycle.CommitAsync(cancellationToken);
            return renewed;
        }

        // El chequeo del código va después de aprovisionar porque la re-invitación necesita
        // excluir a la propia membresía, y sólo se sabe cuál es con el usuario resuelto. Si
        // choca, el usuario recién aprovisionado queda sin membresía y lo recoge
        // OrphanUserCleanupWorker, igual que cuando un invite falla en la base.
        await AdvisorCodeAvailability.EnsureAvailableAsync(
            membershipRepository,
            command.TenantId,
            command.AdvisorCode,
            exceptMembershipId: null,
            cancellationToken);

        // El token plano nace acá y sólo entra al agregado para viajar en el evento de
        // dominio (outbox → email); la fila persiste únicamente su hash.
        var invitationToken = InvitationTokens.Generate();
        var membership = Membership.Invite(
            MembershipId.New(),
            userId,
            command.TenantId,
            command.DisplayName,
            command.Roles,
            Origin,
            invitationToken,
            InvitationTokens.HashOf(invitationToken),
            clock.UtcNow,
            Membership.DefaultInvitationTimeToLive,
            command.AdvisorCode);
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
        await lifecycle.CommitAsync(cancellationToken);
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
        // su bandeja. El nombre y el código del cuerpo se ignoran igual que los roles —tampoco
        // se valida si el código está tomado—: para cambiarle el perfil a un miembro está
        // PUT .../profile (spec 2026-09-24, D6).
        var invitationIsLive =
            existing.State == MembershipState.Invited && now <= existing.ExpiresAt;
        if (invitationIsLive || existing.State == MembershipState.Active)
        {
            return existing.ToDto();
        }

        // Todo lo demás es una invitación vencida (todavía en Invited, porque el vencimiento es
        // perezoso y nadie intentó entrar), una ya marcada como Expired o una membresía quitada.
        // Las tres son renovables (SDD-OD-04; la quitada, por decisión del owner) y vuelven a
        // Invited, así que la persona tiene que aceptar de nuevo. Suspended no: Reinvite la
        // rechaza y se levanta con Reactivate (SDD-OD-13).
        //
        // El código se revisa excluyendo a esta misma membresía: una quitada que vuelve con su
        // propio código no choca consigo misma (D4), pero no puede llevarse el de otra persona.
        // Sin código en el cuerpo no hay nada que revisar: Reinvite conserva el que ya tenía, que
        // es suyo (decisión del developer, 2026-09-24; borrar es sólo por PUT .../profile).
        await AdvisorCodeAvailability.EnsureAvailableAsync(
            membershipRepository,
            command.TenantId,
            command.AdvisorCode,
            existing.Id,
            cancellationToken);

        // Token nuevo en cada renovación: el link vencido muere con su ventana.
        var invitationToken = InvitationTokens.Generate();
        existing.Reinvite(
            command.DisplayName,
            command.Roles,
            invitationToken,
            InvitationTokens.HashOf(invitationToken),
            now,
            Membership.DefaultInvitationTimeToLive,
            command.AdvisorCode);

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

    private async Task EnsureKnownRolesAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
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

        foreach (var role in normalizedRoles)
        {
            if (!await roleReferenceValidator.IsKnownRoleAsync(tenantId, role, cancellationToken))
            {
                throw new TenantDomainException(
                    "tenancy.membership.role_unknown",
                    $"The role '{role}' is not part of the authorization catalog.");
            }
        }
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipInvite))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot invite members to this tenant.");
        }
    }
}
