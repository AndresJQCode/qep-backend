using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>El umbral es obligatorio. <c>FormalNextStep</c> (fase 4b, H6), <c>SurveyEnabled</c> y <c>AutoCloseBusinessDays</c> (fase 4b,
/// H14) son opcionales porque el frontend publicado manda sólo <c>{ dueSoonBusinessDays }</c> (revisión del bloque C, 2026-11-16):
/// ausentes o null conservan lo guardado. Para borrar el «siguiente paso» se manda vacío (<c>""</c>).</summary>
public sealed record UpdateSupportSettingsRequest(
    int? DueSoonBusinessDays, string? FormalNextStep = null, bool? SurveyEnabled = null, int? AutoCloseBusinessDays = null);

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
    int? AutoCloseBusinessDays = null)
    : ICommand<SupportSettingsDto>
{
    /// <summary>Sin umbral, −1: el validador lo rechaza en el campo, en vez de que el dominio lance.</summary>
    public static UpdateSupportSettingsCommand From(Guid tenantId, UpdateSupportSettingsRequest request, long expectedVersion) =>
        new(tenantId, request.DueSoonBusinessDays ?? -1, request.FormalNextStep, expectedVersion, request.SurveyEnabled,
            request.AutoCloseBusinessDays);
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
        // One PUT, one version (owner 2026-11-16): an absent group is passed back as stored, so it never counts as changed.
        var changes = settings.Update(
            command.DueSoonBusinessDays,
            command.FormalNextStep ?? settings.FormalNextStep,
            command.SurveyEnabled ?? settings.SurveyEnabled,
            command.AutoCloseBusinessDays ?? settings.AutoCloseBusinessDays,
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
