using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Arma los DTO de la configuración: la empresa y la ciudad de cada sede, la sede de cada centro y si está en uso. Lo comparten
/// <c>GET /hr/settings</c> y las escrituras, que responden el DTO del recurso.
/// </summary>
public sealed class HrSettingsComposer(
    IHrSettingsRepository settings,
    HrCompanyRefs companies,
    IHrGeographyLookup geography,
    HrSetup setup)
{
    public async Task<HrSettingsDto> ComposeAllAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await settings.ListAsync(tenantId, cancellationToken);
        var activeCompanies = await companies.ListActiveAsync(tenantId, cancellationToken);
        var siteCompanies = await companies.ResolveAsync(tenantId, snapshot.Sites.Select(site => site.CompanyId), cancellationToken);
        var cities = await geography.FindCitiesAsync(
            snapshot.Sites.Where(site => site.CityId is not null).Select(site => site.CityId!.Value).Distinct().ToList(),
            cancellationToken);
        var sitesById = snapshot.Sites.ToDictionary(site => site.Id);

        return new HrSettingsDto(
            activeCompanies,
            snapshot.Sites
                .Select(site => ToSiteDto(site, siteCompanies[site.CompanyId], site.CityId is { } id ? cities.GetValueOrDefault(id) : null, snapshot.InUse))
                .OrderBy(dto => dto.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(dto => dto.Id)
                .ToList(),
            snapshot.Workplaces
                .Select(workplace => ToWorkplaceDto(workplace, sitesById[workplace.SiteId], snapshot.InUse.Contains(workplace.Id.Value)))
                .OrderBy(dto => dto.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(dto => dto.Id)
                .ToList(),
            snapshot.Areas
                .Select(area => new AreaDto(area.Id.Value, area.Name, area.Archived, snapshot.InUse.Contains(area.Id.Value), area.Version))
                .OrderBy(dto => dto.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(dto => dto.Id)
                .ToList(),
            snapshot.Positions
                .Select(position => new PositionDto(
                    position.Id.Value, position.Name, position.Archived, snapshot.InUse.Contains(position.Id.Value), position.Version, position.ExamFrequencyMonths))
                .OrderBy(dto => dto.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(dto => dto.Id)
                .ToList(),
            await setup.IsCompleteAsync(tenantId, cancellationToken),
            HrCompensationFunds.Names);
    }

    public async Task<SiteDto> SiteAsync(Guid tenantId, Site site, CancellationToken cancellationToken)
    {
        var company = (await companies.ResolveAsync(tenantId, [site.CompanyId], cancellationToken))[site.CompanyId];
        HrCityRef? city = null;
        if (site.CityId is { } cityId)
        {
            city = (await geography.FindCitiesAsync([cityId], cancellationToken)).GetValueOrDefault(cityId);
        }

        var inUse = await settings.IsInUseAsync(tenantId, site.Id.Value, cancellationToken);
        return ToSiteDto(site, company, city, inUse ? new HashSet<Guid> { site.Id.Value } : []);
    }

    public async Task<WorkplaceDto> WorkplaceAsync(Guid tenantId, Workplace workplace, CancellationToken cancellationToken)
    {
        var site = await settings.FindSiteAsync(tenantId, workplace.SiteId, cancellationToken)
            ?? throw new InvalidOperationException($"Site '{workplace.SiteId}' of workplace '{workplace.Id}' does not exist.");
        return ToWorkplaceDto(workplace, site, await settings.IsInUseAsync(tenantId, workplace.Id.Value, cancellationToken));
    }

    public async Task<AreaDto> AreaAsync(Guid tenantId, Area area, CancellationToken cancellationToken) =>
        new(area.Id.Value, area.Name, area.Archived, await settings.IsInUseAsync(tenantId, area.Id.Value, cancellationToken), area.Version);

    public async Task<PositionDto> PositionAsync(Guid tenantId, Position position, CancellationToken cancellationToken) =>
        new(
            position.Id.Value, position.Name, position.Archived, await settings.IsInUseAsync(tenantId, position.Id.Value, cancellationToken),
            position.Version, position.ExamFrequencyMonths);

    /// <summary>La configuración que sea, como el DTO de su tipo: lo usan archivar y desarchivar, que responden el recurso.</summary>
    public async Task<object> ComposeAsync(Guid tenantId, OrganizationSetting setting, CancellationToken cancellationToken) =>
        setting switch
        {
            Site site => await SiteAsync(tenantId, site, cancellationToken),
            Workplace workplace => await WorkplaceAsync(tenantId, workplace, cancellationToken),
            Area area => await AreaAsync(tenantId, area, cancellationToken),
            Position position => await PositionAsync(tenantId, position, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown setting type '{setting.GetType().Name}'."),
        };

    private static SiteDto ToSiteDto(Site site, CompanyRefDto company, HrCityRef? city, IReadOnlySet<Guid> inUse) => new(
        site.Id.Value,
        company,
        site.Name,
        site.Address,
        city is null ? null : new MunicipalityRefDto(city.CityId, city.Name, city.DepartmentId, city.DepartmentName),
        site.CompensationFund,
        site.Archived,
        inUse.Contains(site.Id.Value),
        site.Version);

    private static WorkplaceDto ToWorkplaceDto(Workplace workplace, Site site, bool inUse) => new(
        workplace.Id.Value,
        workplace.Name,
        new SettingRefDto(site.Id.Value, site.Name, site.Archived),
        workplace.CiiuCode,
        workplace.RiskClass.ToString(),
        workplace.HighRiskPension,
        workplace.Archived,
        inUse,
        workplace.Version);
}
