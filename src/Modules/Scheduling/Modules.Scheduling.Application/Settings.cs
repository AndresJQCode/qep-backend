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
    ISchedulingSettingsRepository settings,
    ISchedulingReminderTemplateStatus templateStatus,
    ISchedulingMemberDirectory members)
    : IQueryHandler<GetSettingsQuery, SettingsDto>
{
    public async Task<SettingsDto> HandleAsync(GetSettingsQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var row = await settings.FindAsync(query.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("The scheduling settings row is missing after provisioning.");
        return SchedulingComposer.Settings(
            row,
            await ReminderTemplateState.ResolveAsync(templateStatus, query.TenantId, row, cancellationToken),
            await ReminderSenderName.ResolveAsync(members, query.TenantId, row, cancellationToken));
    }
}

/// <summary>
/// El nombre del remitente por defecto, resuelto por el directorio de miembros. Devuelve el nombre aunque la membresía ya no esté activa: la
/// pantalla tiene que poder nombrar al remitente que quedó suspendido para pedir que elijan otro. Nulo sin remitente o si el directorio no lo conoce.
/// </summary>
internal static class ReminderSenderName
{
    public static async Task<string?> ResolveAsync(
        ISchedulingMemberDirectory members, Guid tenantId, SchedulingSettings row, CancellationToken cancellationToken)
    {
        if (row.ReminderSenderMemberId is not { } senderId)
        {
            return null;
        }

        var found = await members.FindAsync(tenantId, [senderId], cancellationToken);
        return found.FirstOrDefault(member => member.MemberId == senderId)?.DisplayName;
    }
}

/// <summary>La plantilla sólo se mira si hay una conexión guardada (el dominio la suelta con el recordatorio apagado).</summary>
internal static class ReminderTemplateState
{
    public static async Task<ReminderTemplateStateDto?> ResolveAsync(
        ISchedulingReminderTemplateStatus templateStatus, Guid tenantId, SchedulingSettings row, CancellationToken cancellationToken) =>
        row.ReminderConnectionId is { } connectionId
            ? await templateStatus.FindAsync(tenantId, connectionId, cancellationToken)
            : null;
}

public sealed class UpdateSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ISchedulingSettingsRepository settings,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    ISchedulingReminderTemplateStatus templateStatus,
    ISchedulingMemberDirectory members,
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
        var changed = new List<string>();
        if (row.ConfirmationMode != Enum.Parse<ConfirmationMode>(request.ConfirmationMode!, ignoreCase: false))
        {
            changed.Add("confirmationMode");
        }

        if (row.MinNoticeMinutes != request.MinNoticeMinutes!.Value)
        {
            changed.Add("minNoticeMinutes");
        }

        if (row.HorizonDays != request.HorizonDays!.Value)
        {
            changed.Add("horizonDays");
        }

        if (row.ClientCancelCutoffHours != request.ClientCancelCutoffHours!.Value)
        {
            changed.Add("clientCancelCutoffHours");
        }

        if (row.ReminderEnabled != (request.ReminderEnabled ?? false))
        {
            changed.Add("reminderEnabled");
        }

        if (row.ReminderOffsetHours != request.ReminderOffsetHours!.Value)
        {
            changed.Add("reminderOffsetHours");
        }

        if (row.ReminderConnectionId != request.ReminderConnectionId)
        {
            changed.Add("reminderConnectionId");
        }

        // Se valida antes de tocar la fila: un remitente inexistente, de otro tenant o suspendido es un error del campo, no del recordatorio.
        // Only a changed id is checked: the stored one may have been suspended since, and the form still sends it back untouched.
        if (request.ReminderSenderMemberId is { } senderId && senderId != row.ReminderSenderMemberId)
        {
            var found = await members.FindAsync(command.TenantId, [senderId], cancellationToken);
            if (!found.Any(member => member.MemberId == senderId && member.IsActive))
            {
                SchedulingFieldErrors.Throw("reminderSenderMemberId", "Elige un miembro activo de la organización.");
            }
        }

        if (row.ReminderSenderMemberId != request.ReminderSenderMemberId)
        {
            changed.Add("reminderSenderMemberId");
        }

        // El dominio exige la conexión del recordatorio con su código (422); el validador ya acotó los rangos.
        row.Update(
            Enum.Parse<ConfirmationMode>(request.ConfirmationMode!, ignoreCase: false),
            request.MinNoticeMinutes!.Value,
            request.HorizonDays!.Value,
            request.ClientCancelCutoffHours!.Value,
            request.ReminderEnabled ?? false,
            request.ReminderOffsetHours!.Value,
            request.ReminderConnectionId,
            request.ReminderSenderMemberId,
            now);
        SchedulingAudit.Publish(
            auditPublisher, executionContext, command.TenantId, SchedulingAuditActions.SettingsUpdated,
            SchedulingAuditActions.Resources.Settings, command.TenantId, changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SchedulingComposer.Settings(
            row,
            await ReminderTemplateState.ResolveAsync(templateStatus, command.TenantId, row, cancellationToken),
            await ReminderSenderName.ResolveAsync(members, command.TenantId, row, cancellationToken));
    }
}
