using BuildingBlocks.Application;
using BuildingBlocks.Application.BusinessHours;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>El umbral es obligatorio. <c>FormalNextStep</c> (fase 4b, H6), <c>SurveyEnabled</c> y <c>AutoCloseBusinessDays</c> (fase 4b,
/// H14) son opcionales porque el frontend publicado manda sólo <c>{ dueSoonBusinessDays }</c> (revisión del bloque C, 2026-11-16):
/// ausentes o null conservan lo guardado. Para borrar el «siguiente paso» se manda vacío (<c>""</c>). <c>UnassignedAlert</c> (spec
/// 2026-11-22 §8.6) es opcional por lo mismo: el frontend publicado no lo manda y su ausencia conserva el aviso guardado; con valor
/// viaja entero.</summary>
public sealed record UpdateSupportSettingsRequest(
    int? DueSoonBusinessDays,
    string? FormalNextStep = null,
    bool? SurveyEnabled = null,
    int? AutoCloseBusinessDays = null,
    UnassignedAlertRequest? UnassignedAlert = null,
    TaskNoticeRequest? TaskNotice = null);

/// <summary>Task-notices spec 2026-11-22 §5.4: the block is optional on the PUT (the published frontend does not send it) and absent or
/// null keeps the stored connection; present, a null <c>WhatsAppConnectionId</c> clears it.</summary>
public sealed record TaskNoticeRequest(Guid? WhatsAppConnectionId);

/// <summary>The task-notice block as the command carries it.</summary>
public sealed record TaskNoticeInput(Guid? WhatsAppConnectionId);

/// <summary>Opcional campo a campo para que un cuerpo incompleto salga 422 con el mapa <c>errors</c> y no 400 del binder (como
/// <c>SaveBusinessHoursRequest</c>): sin <c>enabled</c> se lee apagado, sin <c>recipientIds</c> vacío y sin <c>thresholdHours</c> −1, que el
/// validador rechaza en su campo.</summary>
public sealed record UnassignedAlertRequest(bool? Enabled, int? ThresholdHours, Guid? ScheduleId, IReadOnlyList<Guid>? RecipientIds);

/// <summary>The alert as the command carries it: already shaped like <see cref="SupportUnassignedAlertSettings"/>.</summary>
public sealed record UnassignedAlertInput(bool Enabled, int ThresholdHours, Guid? ScheduleId, IReadOnlyList<Guid> RecipientIds);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
/// <param name="FormalNextStep">Null conserva el guardado; vacío lo borra.</param>
/// <param name="SurveyEnabled">Null conserva el guardado.</param>
/// <param name="AutoCloseBusinessDays">Null conserva el guardado; con valor, de 1 a 30.</param>
public sealed record UpdateSupportSettingsCommand(
    Guid TenantId,
    int DueSoonBusinessDays,
    string? FormalNextStep,
    long ExpectedVersion,
    bool? SurveyEnabled = null,
    int? AutoCloseBusinessDays = null,
    UnassignedAlertInput? UnassignedAlert = null,
    TaskNoticeInput? TaskNotice = null)
    : ICommand<SupportSettingsDto>
{
    /// <summary>Sin umbral, −1: el validador lo rechaza en el campo, en vez de que el dominio lance.</summary>
    public static UpdateSupportSettingsCommand From(Guid tenantId, UpdateSupportSettingsRequest request, long expectedVersion) =>
        new(tenantId, request.DueSoonBusinessDays ?? -1, request.FormalNextStep, expectedVersion, request.SurveyEnabled,
            request.AutoCloseBusinessDays,
            request.UnassignedAlert is { } alert
                ? new UnassignedAlertInput(alert.Enabled ?? false, alert.ThresholdHours ?? -1, alert.ScheduleId, alert.RecipientIds ?? [])
                : null,
            request.TaskNotice is { } notice ? new TaskNoticeInput(notice.WhatsAppConnectionId) : null);
}

public sealed class UpdateSupportSettingsValidator : AbstractValidator<UpdateSupportSettingsCommand>
{
    public UpdateSupportSettingsValidator()
    {
        RuleFor(command => command.DueSoonBusinessDays)
            .InclusiveBetween(SupportTenantSettings.MinDueSoonBusinessDays, SupportTenantSettings.MaxDueSoonBusinessDays);
        RuleFor(command => command.FormalNextStep).MustBeAFormalNextStep();
        RuleFor(command => command.AutoCloseBusinessDays)
            .InclusiveBetween(SupportTenantSettings.MinAutoCloseBusinessDays, SupportTenantSettings.MaxAutoCloseBusinessDays)
            .When(command => command.AutoCloseBusinessDays is not null);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
        // The keys of the errors map are the property paths (UnassignedAlert.ThresholdHours, UnassignedAlert.RecipientIds): the screen
        // lowers the first letter of each segment, as it does for the other support forms.
        RuleFor(command => command.UnassignedAlert!).SetValidator(new UnassignedAlertInputValidator()).When(command => command.UnassignedAlert is not null);
    }
}

public sealed class UnassignedAlertInputValidator : AbstractValidator<UnassignedAlertInput>
{
    public UnassignedAlertInputValidator()
    {
        RuleFor(alert => alert.ThresholdHours)
            .InclusiveBetween(SupportUnassignedAlertSettings.MinThresholdHours, SupportUnassignedAlertSettings.MaxThresholdHours)
            .WithMessage(
                $"Las horas van de {SupportUnassignedAlertSettings.MinThresholdHours} a {SupportUnassignedAlertSettings.MaxThresholdHours}.");
        RuleFor(alert => alert.RecipientIds)
            .Must(ids => ids.Count <= SupportUnassignedAlertSettings.MaxRecipients)
            .WithMessage($"Elige hasta {SupportUnassignedAlertSettings.MaxRecipients} personas.")
            .Must(ids => ids.Distinct().Count() == ids.Count && !ids.Contains(Guid.Empty))
            .WithMessage("Cada persona se elige una sola vez.");
        RuleFor(alert => alert.RecipientIds)
            .NotEmpty()
            .When(alert => alert.Enabled)
            .WithMessage("Elige al menos una persona para prender el aviso.");
    }
}

/// <summary>Guarda el umbral de «por vencer» [owner 2026-11-14], el «siguiente paso» de la respuesta formal (fase 4b, H6), la encuesta y
/// el cierre automático (fase 4b, H14) con su auditoría en la misma transacción. La auditoría nombra sólo los campos que cambiaron, nunca el texto. Sin cambios, no sube la
/// versión ni audita.</summary>
public sealed class UpdateSupportSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ISupportProvisioner provisioner,
    ISupportSettingsRepository settingsRepository,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportMemberDirectory members,
    IBusinessHoursProvider businessHours,
    ISupportTaskNoticeChannels taskNoticeChannels,
    SupportSettingsComposer composer,
    IValidator<UpdateSupportSettingsCommand> validator)
    : ICommandHandler<UpdateSupportSettingsCommand, SupportSettingsDto>
{
    public async Task<SupportSettingsDto> HandleAsync(UpdateSupportSettingsCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var settings = await settingsRepository.GetAsync(command.TenantId, cancellationToken);
        if (settings.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The support settings changed after they were loaded.");
        }

        var alert = command.UnassignedAlert is { } input
            ? await ValidatedAlertAsync(command.TenantId, input, cancellationToken)
            : settings.UnassignedAlert;

        // Absent keeps the stored one; a present block with a null id clears it. With an id, it has to be one the tenant lists today (the
        // screen chose it from that list), whatever the state of its template: an unapproved one is accepted and the worker falls back to
        // email until it is approved.
        var taskNoticeConnectionId = settings.TaskNoticeConnectionId;
        if (command.TaskNotice is { } taskNotice)
        {
            if (taskNotice.WhatsAppConnectionId is { } chosen
                && (await taskNoticeChannels.ListAsync(command.TenantId, cancellationToken)).All(connection => connection.ConnectionId != chosen))
            {
                throw new SupportDomainException(
                    SupportErrorCodes.TaskNoticeConnectionInvalid, "The WhatsApp connection is not one of the tenant active connections.");
            }

            taskNoticeConnectionId = taskNotice.WhatsAppConnectionId;
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        // One PUT, one version (owner 2026-11-16): an absent group is passed back as stored, so it never counts as changed.
        var changes = settings.Update(
            command.DueSoonBusinessDays,
            command.FormalNextStep ?? settings.FormalNextStep,
            command.SurveyEnabled ?? settings.SurveyEnabled,
            command.AutoCloseBusinessDays ?? settings.AutoCloseBusinessDays,
            alert,
            taskNoticeConnectionId,
            now);
        var changedFields = new List<string>();
        if (changes.DueSoonBusinessDays)
        {
            changedFields.Add("dueSoonBusinessDays");
        }

        if (changes.FormalNextStep)
        {
            changedFields.Add("formalNextStep");
        }

        if (changes.SurveyEnabled)
        {
            changedFields.Add("surveyEnabled");
        }

        if (changes.AutoCloseBusinessDays)
        {
            changedFields.Add("autoCloseBusinessDays");
        }

        if (changes.UnassignedAlert)
        {
            changedFields.Add("unassignedAlert");
        }

        if (changes.TaskNotice)
        {
            changedFields.Add("taskNotice");
        }

        if (changedFields.Count > 0)
        {
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.SettingsUpdated,
                SupportAuditActions.Resources.Settings,
                command.TenantId.ToString(),
                "success",
                changedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await composer.ComposeAsync(settings, cancellationToken);
    }

    // Spec 2026-11-22 §8.6: the recipients must be among those who can manage tickets today, and the schedule one of the tenant's. Both
    // were chosen from a list the same screen got, so a miss is a domain code and not a field error.
    private async Task<SupportUnassignedAlertSettings> ValidatedAlertAsync(
        Guid tenantId, UnassignedAlertInput input, CancellationToken cancellationToken)
    {
        var managers = (await members.ListManagersAsync(tenantId, cancellationToken)).Select(member => member.MemberId).ToHashSet();
        if (input.RecipientIds.Any(id => !managers.Contains(id)))
        {
            throw new SupportDomainException(
                SupportErrorCodes.AlertRecipientInvalid, "A recipient of the alert cannot manage tickets or is not a member of the tenant.");
        }

        if (input.ScheduleId is { } scheduleId
            && (await businessHours.ListSchedulesAsync(tenantId, cancellationToken)).All(schedule => schedule.Id != scheduleId))
        {
            throw new SupportDomainException(SupportErrorCodes.AlertScheduleInvalid, "The schedule is not one of the tenant's business hours.");
        }

        return new SupportUnassignedAlertSettings(input.Enabled, input.ThresholdHours, input.ScheduleId, input.RecipientIds);
    }
}
