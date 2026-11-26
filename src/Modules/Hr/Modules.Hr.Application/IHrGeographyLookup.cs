namespace Modules.Hr.Application;

public sealed record HrCountryRef(string Code, string Name);

public sealed record HrCityRef(Guid CityId, string Name, string DivipolaName, Guid DepartmentId, string DepartmentName);

/// <summary>Puerto hacia Geography (ciudades y países); el adaptador vive en Bootstrapper.</summary>
public interface IHrGeographyLookup
{
    /// <summary>Las ciudades que existen entre las pedidas: un id inexistente simplemente no está en el resultado.</summary>
    Task<IReadOnlyDictionary<Guid, HrCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken);

    /// <summary>Todas las ciudades: la importación por Excel las compara por nombre.</summary>
    Task<IReadOnlyList<HrCityRef>> ListAllCitiesAsync(CancellationToken cancellationToken);

    /// <summary>El catálogo de países de Geography (D-10), código ISO 3166-1 alfa-2 y nombre en español.</summary>
    Task<IReadOnlyList<HrCountryRef>> ListCountriesAsync(CancellationToken cancellationToken);
}
