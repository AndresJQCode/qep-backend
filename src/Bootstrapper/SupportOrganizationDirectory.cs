using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>The companies the public support form can show (spec 2026-11-15 §9.3, plan L30), calqued from <c>PosCompanyLookup</c>.
/// Every read filters by tenant: a company of another tenant is null. Phase 4b (H5) adds the contact data the formal reply letterhead
/// prints; a Colombian company stores only its city id (<c>Company.CityName</c> is for foreign ones), so the name comes from
/// Geography.</summary>
internal sealed class SupportOrganizationDirectory(ICompanyRepository companies, ICityRepository cities) : ISupportOrganizationDirectory
{
    public async Task<IReadOnlyList<SupportOrganizationRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var found = await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken);
        // One query for the distinct cities of the list, not one per company.
        var cityIds = found
            .Where(company => company.CityId is not null)
            .Select(company => new CityId(company.CityId!.Value))
            .ToHashSet();
        var cityNames = cityIds.Count == 0
            ? []
            : (await cities.ListByIdsAsync(cityIds, cancellationToken)).ToDictionary(city => city.Id.Value, city => city.Name);

        return found
            .Select(company => ToRef(company, CityNameOf(company, cityNames)))
            .OrderBy(company => company.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(company => company.Id)
            .ToList();
    }

    public async Task<SupportOrganizationRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        if (await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken) is not { } company)
        {
            return null;
        }

        var cityName = company.CityName;
        if (cityName is null && company.CityId is { } cityId)
        {
            cityName = (await cities.FindAsync(new CityId(cityId), cancellationToken))?.Name;
        }

        return ToRef(company, cityName);
    }

    private static string? CityNameOf(Company company, Dictionary<Guid, string> cityNames) =>
        company.CityName ?? (company.CityId is { } cityId && cityNames.TryGetValue(cityId, out var name) ? name : null);

    private static SupportOrganizationRef ToRef(Company company, string? cityName) =>
        new(
            company.Id.Value,
            company.Name,
            company.DocumentType.ToWire(),
            company.TaxId,
            company.IsActive,
            company.Address,
            company.Email,
            company.Phone,
            cityName);
}
