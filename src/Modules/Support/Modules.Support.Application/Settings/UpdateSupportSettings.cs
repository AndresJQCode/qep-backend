using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record UpdateSupportSettingsRequest(int? DueSoonBusinessDays);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record UpdateSupportSettingsCommand(Guid TenantId, int DueSoonBusinessDays, long ExpectedVersion)
    : ICommand<SupportSettingsDto>
{
    /// <summary>Sin valor, −1: el validador lo rechaza en el campo, en vez de que el dominio lance.</summary>
    public static UpdateSupportSettingsCommand From(Guid tenantId, UpdateSupportSettingsRequest request, long expectedVersion) =>
        new(tenantId, request.DueSoonBusinessDays ?? -1, expectedVersion);
}

public sealed class UpdateSupportSettingsValidator : AbstractValidator<UpdateSupportSettingsCommand>
{
    public UpdateSupportSettingsValidator()
    {
        RuleFor(command => command.DueSoonBusinessDays)
            .InclusiveBetween(SupportTenantSettings.MinDueSoonBusinessDays, SupportTenantSettings.MaxDueSoonBusinessDays);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>Guarda el umbral de «por vencer» [owner 2026-11-14] con su auditoría en la misma transacción. Sin cambios, no sube la
/// versión ni audita.</summary>
public sealed class UpdateSupportSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IClock clock,
    ISupportProvisioner provisioner,
    ISupportSettingsRepository settingsRepository,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    IValidator<UpdateSupportSettingsCommand> validator)
    : ICommandHandler<UpdateSupportSettingsCommand, SupportSettingsDto>
{
    private static readonly string[] ChangedFields = ["dueSoonBusinessDays"];

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

        var version = settings.Version;
        var now = clock.UtcNow;
        settings.ChangeDueSoon(command.DueSoonBusinessDays, now);
        if (settings.Version != version)
        {
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.SettingsUpdated,
                SupportAuditActions.Resources.Settings,
                command.TenantId.ToString(),
                "success",
                ChangedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return SupportDtoMapping.ToDto(settings);
    }
}
