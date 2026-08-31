namespace Modules.Companies.Application;

/// <summary>
/// Resuelve ciudad y departamento del módulo <c>Geography</c> para las respuestas de
/// <c>Company</c> que devuelven la ciudad resuelta.
///
/// Puerto en Application, adaptador en Bootstrapper — mismo patrón que
/// <c>ICustomerGeographyLookup</c> entre Customers y Geography. <c>Modules.Companies.Application</c>
/// no puede referenciar <c>Modules.Geography.Application</c> directamente: ningún módulo de
/// negocio referencia a otro, así que el acoplamiento queda en el composition root — que ya
/// referencia a los dos y cuyo trabajo es exactamente cablearlos. El adaptador
/// (<c>CompanyGeographyLookup</c>) vive en <c>Bootstrapper</c>.
/// </summary>
public interface ICompanyGeographyLookup
{
    Task<CompanyCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken);

    /// <summary>
    /// La versión en lote de <see cref="FindCityAsync"/>, para que <c>ListCompaniesHandler</c>
    /// resuelva la página entera con una sola consulta en vez de una por empresa.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CompanyCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken);
}

/// <summary>
/// La ciudad y su departamento, traducidos al vocabulario de <c>companies</c>. Trae los dos
/// juntos porque las respuestas de empresa necesitan los dos objetos resueltos — pedirlos por
/// separado sería una segunda consulta por empresa.
/// </summary>
public sealed record CompanyCityRef(
    Guid CityId,
    string CityDivipolaCode,
    string CityName,
    Guid DepartmentId,
    string DepartmentDivipolaCode,
    string DepartmentName);
