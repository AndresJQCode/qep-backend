using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record UpdateMemberProfileCommand(
    TenantId TenantId,
    MembershipId MembershipId,
    string DisplayName,
    int? AdvisorCode,
    long ExpectedVersion,
    string CorrelationId) : ICommand<MembershipListItemDto>;

/// <summary>
/// Texto libre y un número, así que lleva validador aunque el dominio ya valide: el dominio da el
/// código (<c>display_name_invalid</c>, <c>advisor_code_invalid</c>), el validador da el campo
/// (<c>errors.DisplayName</c>, <c>errors.AdvisorCode</c>), que es lo único con lo que el diálogo
/// sabe marcar el input.
/// </summary>
public sealed class UpdateMemberProfileValidator : AbstractValidator<UpdateMemberProfileCommand>
{
    public UpdateMemberProfileValidator()
    {
        RuleFor(command => command.DisplayName)
            .NotEmpty()
            .MaximumLength(Membership.DisplayNameMaxLength);
        RuleFor(command => command.AdvisorCode)
            .GreaterThan(0)
            .When(command => command.AdvisorCode is not null);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Edita el perfil de un miembro del roster —nombre y código de asesor— en un solo PUT (spec
/// 2026-09-24, D6). Uno solo y no dos porque el diálogo edita los dos campos juntos: con dos PUT,
/// el primero sube la versión y el segundo viaja con el If-Match viejo, y la pantalla se pisa
/// sola con un 412. Nombre y código comparten <c>AdvisorshipManage</c>, así que no hay dos
/// permisos que separar, que es lo que sí separó a display-name de roles.
///
/// Reemplaza a <see cref="UpdateMemberDisplayNameHandler"/>, que convive hasta que el frontend
/// migre (D7). Se permite en cualquier estado y sobre la propia membresía, igual que aquél.
/// </summary>
public sealed class UpdateMemberProfileHandler(
    IMembershipRepository membershipRepository,
    ITenantRepository tenantRepository,
    IUserDirectory userDirectory,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateMemberProfileCommand> validator)
    : ICommandHandler<UpdateMemberProfileCommand, MembershipListItemDto>
{
    public async Task<MembershipListItemDto> HandleAsync(
        UpdateMemberProfileCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var tenant = await TenantLoader.LoadAsync(
            tenantRepository, command.TenantId, cancellationToken);
        var membership = await MembershipLoader.LoadAsync(
            membershipRepository, command.MembershipId, command.TenantId, cancellationToken);

        if (membership.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The membership changed after it was loaded.");
        }

        // Sólo se pregunta si el código cambia: guardar el propio no choca consigo mismo, y así
        // un guardado sin cambios no cuesta una consulta.
        if (command.AdvisorCode != membership.AdvisorCode)
        {
            await AdvisorCodeAvailability.EnsureAvailableAsync(
                membershipRepository,
                command.TenantId,
                command.AdvisorCode,
                membership.Id,
                cancellationToken);
        }

        var now = clock.UtcNow;
        // Sólo se audita y se guarda lo que cambió: un guardado sin cambios es una no-op del
        // agregado, y registrarla dejaría en la auditoría un cambio que no ocurrió.
        if (membership.UpdateProfile(command.DisplayName, command.AdvisorCode, now))
        {
            auditRecorder.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                "tenancy.membership.profile_updated",
                "membership",
                membership.Id.ToString(),
                "success",
                [],
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var email = await userDirectory.GetEmailAsync(membership.UserId, cancellationToken);
        return membership.ToListItemDto(email, tenant);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipManage))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot update member profiles of this tenant.");
        }
    }
}
