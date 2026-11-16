using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>The companies the public support form can show (spec 2026-11-15 §9.3, plan L30), calqued from <c>PosCompanyLookup</c>.
/// Every read filters by tenant: a company of another tenant is null.</summary>
internal sealed class SupportOrganizationDirectory(ICompanyRepository companies) : ISupportOrganizationDirectory
{
    public async Task<IReadOnlyList<SupportOrganizationRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken))
            .Select(ToRef)
            .OrderBy(company => company.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(company => company.Id)
            .ToList();

    public async Task<SupportOrganizationRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken) is { } company
            ? ToRef(company)
            : null;

    private static SupportOrganizationRef ToRef(Company company) =>
        new(company.Id.Value, company.Name, company.DocumentType.ToWire(), company.TaxId, company.IsActive);
}
