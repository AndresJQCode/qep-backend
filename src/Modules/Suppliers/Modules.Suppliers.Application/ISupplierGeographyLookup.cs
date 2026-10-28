namespace Modules.Suppliers.Application;

/// <summary>
/// Resuelve ciudad y departamento del módulo <c>Geography</c> para las respuestas de proveedor.
/// Puerto en Application, adaptador en Bootstrapper (mismo patrón que <c>ICompanyGeographyLookup</c>):
/// ningún módulo de negocio referencia a otro, así que el acoplamiento queda en el composition root.
/// </summary>
public interface ISupplierGeographyLookup
{
    Task<SupplierCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken);

    /// <summary>La versión en lote de <see cref="FindCityAsync"/>: la lista resuelve la página entera con una consulta, sin N+1.</summary>
    Task<IReadOnlyDictionary<Guid, SupplierCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds,
        CancellationToken cancellationToken);
}

/// <summary>La ciudad y su departamento juntos: las respuestas necesitan los dos objetos y pedirlos aparte sería una segunda consulta por proveedor.</summary>
public sealed record SupplierCityRef(
    Guid CityId,
    string CityDivipolaCode,
    string CityName,
    Guid DepartmentId,
    string DepartmentDivipolaCode,
    string DepartmentName);
