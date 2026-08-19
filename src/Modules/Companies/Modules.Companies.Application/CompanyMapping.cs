using Modules.Companies.Domain;

namespace Modules.Companies.Application;

internal static class CompanyMapping
{
    public static CompanyDto ToDto(this Company company) => new(
        company.Id.Value,
        company.Name,
        company.AccountNumber,
        company.TaxId,
        company.IsActive,
        company.Phone,
        company.Email,
        company.Address,
        company.CreatedAt,
        company.UpdatedAt);
}
