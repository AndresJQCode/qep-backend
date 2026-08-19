using Modules.Companies.Domain;

namespace Modules.Companies.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar.
public interface ICompanyRepository
{
    Task<IReadOnlyList<Company>> SearchAsync(
        Guid tenantId,
        string? search,
        CompanyStatusFilter? status,
        CancellationToken cancellationToken);

    Task<Company?> FindAsync(
        Guid tenantId,
        CompanyId companyId,
        CancellationToken cancellationToken);

    void Add(Company company);
}
