using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Companies → Wholesale: la empresa facturadora que da la identidad del proveedor vinculado (D-2). <c>Eligible</c> dice si su
/// documento lo admite un proveedor (los de <c>IdentificationTypeParser.SupportedWireValues</c> de Suppliers: los siete códigos,
/// así que una empresa de cualquier país puede ser mayorista).
/// </summary>
internal sealed class WholesaleCompanyLookup(ICompanyRepository companies) : IWholesaleCompanyLookup
{
    public async Task<WholesaleCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken);
        return company is null ? null : ToRef(company);
    }

    public async Task<IReadOnlyList<WholesaleCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken))
            .Select(ToRef)
            .ToList();

    private static WholesaleCompanyRef ToRef(Company company)
    {
        var documentType = company.DocumentType.ToWire();
        return new WholesaleCompanyRef(
            company.Id.Value,
            company.Name,
            documentType,
            company.TaxId,
            company.Country,
            company.CityId,
            company.CityName,
            company.Phone,
            company.Email,
            company.Address,
            company.IsActive,
            Modules.Suppliers.Domain.IdentificationTypeParser.SupportedWireValues.Contains(
                documentType, StringComparer.OrdinalIgnoreCase));
    }
}
