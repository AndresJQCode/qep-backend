using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>El <c>PUT</c> reemplaza los ajustes enteros: <c>FormalNextStep</c> ausente o null borra el «siguiente paso» (fase 4b, H6).
/// <c>SurveyEnabled</c> y <c>AutoCloseBusinessDays</c> (fase 4b, H14) son obligatorios: sin ellos, 422 en su campo.</summary>
public sealed record UpdateSupportSettingsRequest(
    int? DueSoonBusinessDays, string? FormalNextStep = null, bool? SurveyEnabled = null, int? AutoCloseBusinessDays = null);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record UpdateSupportSettingsCommand(
    Guid TenantId,
    int DueSoonBusinessDays,
    string? FormalNextStep,
    long ExpectedVersion,
    bool? SurveyEnabled = null,
    int AutoCloseBusinessDays = -1)
    : ICommand<SupportSettingsDto>
{
    /// <summary>Sin valor, −1 (y la encuesta null): el validador lo rechaza en el campo, en vez de que el dominio lance.</summary>
    public static UpdateSupportSettingsCommand From(Guid tenantId, UpdateSupportSettingsRequest request, long expectedVersion) =>
        new(tenantId, request.DueSoonBusinessDays ?? -1, request.FormalNextStep, expectedVersion, request.SurveyEnabled,
            request.AutoCloseBusinessDays ?? -1);
}

public sealed class UpdateSupportSettingsValidator : AbstractValidator<UpdateSupportSettingsCommand>
{
    public UpdateSupportSettingsValidator()
    {
        RuleFor(command => command.DueSoonBusinessDays)
            .InclusiveBetween(SupportTenantSettings.MinDueSoonBusinessDays, SupportTenantSettings.MaxDueSoonBusinessDays);
        RuleFor(command => command.FormalNextStep).MustBeAFormalNextStep();
        RuleFor(command => command.SurveyEnabled).NotNull();
        RuleFor(command => command.AutoCloseBusinessDays)
            .InclusiveBetween(SupportTenantSettings.MinAutoCloseBusinessDays, SupportTenantSettings.MaxAutoCloseBusinessDays);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
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

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var changedFields = new List<string>();
        var version = settings.Version;
        settings.ChangeDueSoon(command.DueSoonBusinessDays, now);
        if (settings.Version != version)
        {
            changedFields.Add("dueSoonBusinessDays");
        }

        version = settings.Version;
        settings.ChangeFormalNextStep(command.FormalNextStep, now);
        if (settings.Version != version)
        {
            changedFields.Add("formalNextStep");
        }

        var (surveyEnabled, autoClose) = (settings.SurveyEnabled, settings.AutoCloseBusinessDays);
        settings.ChangeSurvey(command.SurveyEnabled!.Value, command.AutoCloseBusinessDays, now);
        if (settings.SurveyEnabled != surveyEnabled)
        {
            changedFields.Add("surveyEnabled");
        }

        if (settings.AutoCloseBusinessDays != autoClose)
        {
            changedFields.Add("autoCloseBusinessDays");
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

        return SupportDtoMapping.ToDto(settings);
    }
}
