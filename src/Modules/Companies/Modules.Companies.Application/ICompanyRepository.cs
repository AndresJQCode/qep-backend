using Modules.Companies.Domain;

namespace Modules.Companies.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar.
public interface ICompanyRepository
{
    /// <summary>
    /// <c>name</c>/<c>taxId</c> son dos filtros independientes (CLI-FILTROS-01, mismo criterio
    /// que <c>ICustomerRepository.SearchAsync</c>): cada uno filtra su propia columna con ILIKE
    /// y se combinan con AND cuando el llamador manda los dos. <c>search</c> es el criterio OR
    /// original (nombre o numero de cuenta) que la grilla ya usaba.
    /// </summary>
    Task<IReadOnlyList<Company>> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? taxId,
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
