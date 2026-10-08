using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Pos.Application;

namespace Bootstrapper;

internal sealed class PosCompanyLookup(ICompanyRepository companies) : IPosCompanyLookup
{
    public async Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken))
            .Select(ToRef)
            .ToList();

    public async Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken) is { } company
            ? ToRef(company)
            : null;

    private static PosCompanyRef ToRef(Company company) =>
        new(company.Id.Value, company.Name, company.TaxId, company.Address, company.Phone, company.IsActive);
}
