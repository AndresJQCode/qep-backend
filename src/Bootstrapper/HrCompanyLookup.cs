using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Hr.Application;

namespace Bootstrapper;

/// <summary>Adapta Companies al puerto de Hr (calco de <see cref="PosCompanyLookup"/>): sólo razón social, NIT y si está activa.</summary>
internal sealed class HrCompanyLookup(ICompanyRepository companies) : IHrCompanyLookup
{
    public async Task<IReadOnlyList<HrCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken))
            .Select(ToRef)
            .ToList();

    public async Task<HrCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken) is { } company
            ? ToRef(company)
            : null;

    private static HrCompanyRef ToRef(Company company) => new(company.Id.Value, company.Name, company.TaxId, company.IsActive,
        string.IsNullOrWhiteSpace(company.Country) ? Company.ColombiaCountryCode : company.Country);
}
