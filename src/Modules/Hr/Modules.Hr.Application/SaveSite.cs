using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record CreateSiteCommand(Guid TenantId, SiteRequest Request) : ICommand<SiteDto>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateSiteCommand(Guid TenantId, Guid SiteId, SiteRequest Request, string? IfMatch) : ICommand<SiteDto>;

/// <summary>
/// Crear una sede exige una empresa <b>activa</b> (D-12: un vínculo nuevo). La empresa no cambia después: editar una sede
/// existente con la empresa desactivada sigue funcionando, porque no se revalida si es la misma.
/// </summary>
public sealed class CreateSiteHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    HrCompanyRefs companies,
    IHrGeographyLookup geography,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer,
    IValidator<SiteRequest> validator)
    : ICommandHandler<CreateSiteCommand, SiteDto>
{
    public async Task<SiteDto> HandleAsync(CreateSiteCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var company = await companies.RequireActiveAsync(command.TenantId, request.CompanyId!.Value, "companyId", cancellationToken);
        var cityId = await HrSettingsSupport.EnsureCityAsync(geography, request.CityId, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        HrSiteCompensationFund.Ensure(company.Country, request.CompensationFund);
        var site = Site.Create(
            SiteId.New(), command.TenantId, company.Id, request.Name!, HrText.Clean(request.Address), cityId,
            HrText.Clean(request.CompensationFund), now);
        settings.Add(site);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsCreated, HrAuditActions.Resources.Site,
            site.Id.Value, ["companyId", "name", "address", "cityId", "compensationFund"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.SiteAsync(command.TenantId, site, cancellationToken);
    }
}

public sealed class UpdateSiteHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    HrCompanyRefs companies,
    IHrGeographyLookup geography,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer,
    IValidator<SiteRequest> validator)
    : ICommandHandler<UpdateSiteCommand, SiteDto>
{
    public async Task<SiteDto> HandleAsync(UpdateSiteCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var site = await settings.FindSiteAsync(command.TenantId, new SiteId(command.SiteId), cancellationToken)
            ?? throw HrNotFound.Site(command.SiteId);
        HrSettingsSupport.EnsureVersion(site.Version, expectedVersion);
        if (request.CompanyId != site.CompanyId)
        {
            HrFieldErrors.Throw("companyId", "La empresa de una sede no cambia.");
        }

        // La empresa de una sede es fija: el país que manda es el de la guardada, aunque esté desactivada.
        var company = (await companies.ResolveAsync(command.TenantId, [site.CompanyId], cancellationToken))[site.CompanyId];
        HrSiteCompensationFund.Ensure(company.Country, request.CompensationFund);
        var cityId = await HrSettingsSupport.EnsureCityAsync(geography, request.CityId, cancellationToken);
        var address = HrText.Clean(request.Address);
        var fund = HrText.Clean(request.CompensationFund);
        var changed = new List<string>(4);
        HrSettingsSupport.Diff(changed, "name", site.Name, request.Name!.Trim());
        HrSettingsSupport.Diff(changed, "address", site.Address, address);
        HrSettingsSupport.Diff(changed, "cityId", site.CityId, cityId);
        HrSettingsSupport.Diff(changed, "compensationFund", site.CompensationFund, fund);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        site.Update(request.Name!, address, cityId, fund, now);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsUpdated, HrAuditActions.Resources.Site,
            site.Id.Value, changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.SiteAsync(command.TenantId, site, cancellationToken);
    }
}
