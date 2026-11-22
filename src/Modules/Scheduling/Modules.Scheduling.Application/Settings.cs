using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record GetSettingsQuery(Guid TenantId) : IQuery<SettingsDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateSettingsCommand(Guid TenantId, SettingsRequest Request, string? IfMatch) : ICommand<SettingsDto>;

public sealed class GetSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ISchedulingSettingsRepository settings)
    : IQueryHandler<GetSettingsQuery, SettingsDto>
{
    public async Task<SettingsDto> HandleAsync(GetSettingsQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var row = await settings.FindAsync(query.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("The scheduling settings row is missing after provisioning.");
        return SchedulingComposer.Settings(row);
    }
}

public sealed class UpdateSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ISchedulingSettingsRepository settings,
    ISchedulingUnitOfWork unitOfWork,
    IValidator<SettingsRequest> validator)
    : ICommandHandler<UpdateSettingsCommand, SettingsDto>
{
    public async Task<SettingsDto> HandleAsync(UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.SettingsManage);
        var expectedVersion = SchedulingIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        await firstAccess.EnsureAsync(command.TenantId, cancellationToken);

        var row = await settings.FindAsync(command.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("The scheduling settings row is missing after provisioning.");
        SchedulingIfMatch.EnsureVersion(row.Version, expectedVersion);

        var request = command.Request;
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        // El dominio exige la conexión y la plantilla del recordatorio con su código (422); el validador ya acotó los rangos.
        row.Update(
            Enum.Parse<ConfirmationMode>(request.ConfirmationMode!, ignoreCase: false),
            request.MinNoticeMinutes!.Value,
            request.HorizonDays!.Value,
            request.ClientCancelCutoffHours!.Value,
            request.ReminderEnabled ?? false,
            request.ReminderOffsetHours!.Value,
            request.ReminderConnectionId,
            request.ReminderTemplateId,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Settings(row);
    }
}
