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

    // Borrado en duro, y con el agregado ya cargado por FindAsync: un Remove por id necesitaría
    // una entidad stub y perdería el filtro por tenant que hace el Find. Si alguna clave foránea
    // apunta a la empresa, quien frena es PostgreSQL en el commit — ver DeleteCompanyHandler.
    void Remove(Company company);
}
