using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record CreateWorkplaceCommand(Guid TenantId, WorkplaceRequest Request) : ICommand<WorkplaceDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateWorkplaceCommand(Guid TenantId, Guid WorkplaceId, WorkplaceRequest Request, string? IfMatch)
    : ICommand<WorkplaceDto>;

public sealed class CreateWorkplaceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer,
    IValidator<WorkplaceRequest> validator)
    : ICommandHandler<CreateWorkplaceCommand, WorkplaceDto>
{
    public async Task<WorkplaceDto> HandleAsync(CreateWorkplaceCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var site = await settings.FindSiteAsync(command.TenantId, new SiteId(request.SiteId!.Value), cancellationToken);
        if (site is null)
        {
            HrFieldErrors.Throw("siteId", "La sede no existe.");
        }

        _ = HrEnums.TryParse<ArlRiskClass>(request.RiskClass, out var riskClass);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        // Crear en una sede archivada es hr.settings.site_archived (422), una regla del dominio.
        var workplace = Workplace.Create(
            WorkplaceId.New(), command.TenantId, site, request.Name!, HrText.Clean(request.CiiuCode), riskClass,
            request.HighRiskPension ?? false, now);
        settings.Add(workplace);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsCreated, HrAuditActions.Resources.Workplace,
            workplace.Id.Value, ["siteId", "name", "ciiuCode", "riskClass", "highRiskPension"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.WorkplaceAsync(command.TenantId, workplace, cancellationToken);
    }
}

public sealed class UpdateWorkplaceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer,
    IValidator<WorkplaceRequest> validator)
    : ICommandHandler<UpdateWorkplaceCommand, WorkplaceDto>
{
    public async Task<WorkplaceDto> HandleAsync(UpdateWorkplaceCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var workplace = await settings.FindWorkplaceAsync(command.TenantId, new WorkplaceId(command.WorkplaceId), cancellationToken)
            ?? throw HrNotFound.Workplace(command.WorkplaceId);
        HrSettingsSupport.EnsureVersion(workplace.Version, expectedVersion);
        if (request.SiteId != workplace.SiteId.Value)
        {
            HrFieldErrors.Throw("siteId", "La sede de un centro de trabajo no cambia.");
        }

        _ = HrEnums.TryParse<ArlRiskClass>(request.RiskClass, out var riskClass);
        var ciiuCode = HrText.Clean(request.CiiuCode);
        var highRiskPension = request.HighRiskPension ?? false;
        var changed = new List<string>(4);
        HrSettingsSupport.Diff(changed, "name", workplace.Name, request.Name!.Trim());
        HrSettingsSupport.Diff(changed, "ciiuCode", workplace.CiiuCode, ciiuCode);
        HrSettingsSupport.Diff(changed, "riskClass", workplace.RiskClass, riskClass);
        HrSettingsSupport.Diff(changed, "highRiskPension", workplace.HighRiskPension, highRiskPension);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        workplace.Update(request.Name!, ciiuCode, riskClass, highRiskPension, now);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsUpdated, HrAuditActions.Resources.Workplace,
            workplace.Id.Value, changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.WorkplaceAsync(command.TenantId, workplace, cancellationToken);
    }
}
