using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record UpdateMemberRolesCommand(
    TenantId TenantId,
    MembershipId MembershipId,
    IReadOnlyCollection<string> Roles,
    long ExpectedVersion,
    string CorrelationId) : ICommand<MembershipListItemDto>;

public sealed class UpdateMemberRolesValidator : AbstractValidator<UpdateMemberRolesCommand>
{
    public UpdateMemberRolesValidator()
    {
        RuleFor(command => command.Roles).NotNull().NotEmpty();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
        RuleForEach(command => command.Roles)
            .NotEmpty()
            .MaximumLength(120);
    }
}

public sealed class UpdateMemberRolesHandler(
    IMembershipRepository membershipRepository,
    ITenantRepository tenantRepository,
    IUserDirectory userDirectory,
    IRolePermissionChecker rolePermissionChecker,
    IRoleReferenceValidator roleReferenceValidator,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    IValidator<UpdateMemberRolesCommand> validator,
    ITenantAccessVersion tenantAccessVersion,
    MembershipPhoneReader phones)
    : ICommandHandler<UpdateMemberRolesCommand, MembershipListItemDto>
{
    public async Task<MembershipListItemDto> HandleAsync(
        UpdateMemberRolesCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var requestedRoles = command.Roles
            .Select(role => role.Trim())
            .Where(role => role.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedRoles.Length == 0)
        {
            throw new TenantDomainException(
                "tenancy.membership.roles_required",
                "A membership requires at least one role.");
        }

        var tenant = await TenantLoader.LoadAsync(
            tenantRepository, command.TenantId, cancellationToken);
        var membership = await MembershipLoader.LoadAsync(
            membershipRepository, command.MembershipId, command.TenantId, cancellationToken);

        // Antes que los roles: role_unavailable compara contra los roles guardados, y con una versión
        // vieja el cliente no vio esos roles. Una pantalla desactualizada recibe 412, no 422.
        if (membership.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "Membership roles changed after they were loaded.");
        }

        foreach (var role in requestedRoles)
        {
            var reference = await roleReferenceValidator.CheckAsync(
                command.TenantId, role, cancellationToken);
            if (reference == RoleReference.Unknown)
            {
                throw new TenantDomainException(
                    "tenancy.membership.role_unknown",
                    $"The role '{role}' is not part of the authorization catalog.");
            }

            // Spec 2026-11-15: sólo se rechaza lo que se agrega. Quien ya tiene un rol que dejó de
            // conceder algo (Cajero con pos apagado) lo conserva, y la pantalla lo reenvía al guardar.
            if (reference == RoleReference.Unassignable &&
                !membership.Roles.Contains(role, StringComparer.Ordinal))
            {
                throw new TenantDomainException(
                    "tenancy.membership.role_unavailable",
                    $"The role '{role}' grants nothing in this tenant: its module is not enabled.");
            }
        }

        if (membership.UserId == executionContext.SubjectId)
        {
            throw new TenantDomainException(
                "tenancy.membership.cannot_target_self",
                "A member cannot change their own roles.");
        }

        var currentlyCanManage = membership.State == MembershipState.Active &&
            await rolePermissionChecker.AnyGrantsAsync(
                command.TenantId, membership.Roles, TenancyPermissions.AdvisorshipManage,
                cancellationToken);
        var willManage = await rolePermissionChecker.AnyGrantsAsync(
            command.TenantId, requestedRoles, TenancyPermissions.AdvisorshipManage,
            cancellationToken);
        if (currentlyCanManage && !willManage)
        {
            var others = await membershipRepository.ListActiveExcludingAsync(
                command.TenantId, command.MembershipId, cancellationToken);
            // Recorrido secuencial y no `Any` sincronico: resolver permisos ahora consulta
            // el catalogo del tenant. Corta en el primero que administra, asi que el caso
            // normal —hay otro admin— sigue costando una sola resolucion.
            var hasOtherManager = false;
            foreach (var other in others)
            {
                if (await rolePermissionChecker.AnyGrantsAsync(
                    command.TenantId, other.Roles, TenancyPermissions.AdvisorshipManage,
                    cancellationToken))
                {
                    hasOtherManager = true;
                    break;
                }
            }

            if (!hasOtherManager)
            {
                throw new TenantDomainException(
                    "tenancy.membership.last_active_manager",
                    "The tenant must retain at least one member who can manage memberships.");
            }
        }

        var now = clock.UtcNow;
        membership.ChangeRoles(tenant, requestedRoles, now);

        auditRecorder.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            "tenancy.membership.roles_changed",
            "membership",
            membership.Id.ToString(),
            "success",
            [],
            now);

        foreach (var domainEvent in membership.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, command.CorrelationId);
        }

        // Spec 2026-11-13, D-5: la caché de permisos del tenant, antes del guardado y otra vez después.
        await tenantAccessVersion.BumpAsync(command.TenantId.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await tenantAccessVersion.BumpAsync(command.TenantId.Value, cancellationToken);
        var email = await userDirectory.GetEmailAsync(membership.UserId, cancellationToken);
        return membership.ToListItemDto(
            email, tenant, await phones.ForAsync(membership, cancellationToken));
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipManage))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot manage membership roles for this tenant.");
        }
    }
}
