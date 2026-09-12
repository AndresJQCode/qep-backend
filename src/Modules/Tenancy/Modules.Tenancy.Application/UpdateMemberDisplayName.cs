using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record UpdateMemberDisplayNameCommand(
    TenantId TenantId,
    MembershipId MembershipId,
    string DisplayName,
    long ExpectedVersion,
    string CorrelationId) : ICommand<MembershipListItemDto>;

/// <summary>
/// Texto libre, así que lleva validador aunque el dominio ya valide: el dominio da el código
/// (<c>display_name_invalid</c>), el validador da el campo (<c>errors.DisplayName</c>), que es
/// lo único con lo que el diálogo sabe marcar el input.
/// </summary>
public sealed class UpdateMemberDisplayNameValidator
    : AbstractValidator<UpdateMemberDisplayNameCommand>
{
    public UpdateMemberDisplayNameValidator()
    {
        RuleFor(command => command.DisplayName)
            .NotEmpty()
            .MaximumLength(Membership.DisplayNameMaxLength);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Renombra a un miembro del roster (spec 2026-09-11, D4). Endpoint propio y no parte de
/// <c>PATCH .../roles</c>: son dos permisos distintos, y un PATCH mezclado obligaría a elegir la
/// autorización según los campos que vinieron.
///
/// Se permite en cualquier estado —el nombre es presentación y no cambia el acceso— y también
/// sobre la propia membresía, a diferencia de los roles: el owner carga su nombre desde acá.
/// </summary>
public sealed class UpdateMemberDisplayNameHandler(
    IMembershipRepository membershipRepository,
    IUserDirectory userDirectory,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateMemberDisplayNameCommand> validator)
    : ICommandHandler<UpdateMemberDisplayNameCommand, MembershipListItemDto>
{
    public async Task<MembershipListItemDto> HandleAsync(
        UpdateMemberDisplayNameCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var membership = await MembershipLoader.LoadAsync(
            membershipRepository, command.MembershipId, command.TenantId, cancellationToken);

        if (membership.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The membership changed after it was loaded.");
        }

        var now = clock.UtcNow;
        // Sólo se audita y se guarda lo que cambió: renombrar al mismo nombre es una no-op del
        // agregado, y registrarla dejaría en la auditoría un cambio que no ocurrió.
        if (membership.Rename(command.DisplayName, now))
        {
            auditRecorder.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                "tenancy.membership.renamed",
                "membership",
                membership.Id.ToString(),
                "success",
                [],
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

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
                "The subject cannot rename members of this tenant.");
        }
    }
}
