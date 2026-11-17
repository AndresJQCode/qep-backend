namespace Modules.Catalog.Application;

/// <summary>
/// Resuelve el municipio de una tasa municipal (ReteICA) contra <c>Geography</c>: para validar el <c>cityId</c> al crear y
/// para pintar el nombre en las respuestas (spec ReteICA por empresa §7.2).
///
/// Puerto en Application, adaptador en Bootstrapper (<c>TaxRateGeographyLookup</c>), mismo patrón que
/// <c>ICompanyGeographyLookup</c>: Catalog no referencia a Geography. Geography sólo tiene la DIVIPOLA, así que «encontrada»
/// quiere decir «existe y es de Colombia».
/// </summary>
public interface ITaxRateGeographyLookup
{
    /// <summary>Las ciudades encontradas, por id; las que no existen no aparecen. Sin ids no consulta nada.</summary>
    Task<IReadOnlyDictionary<Guid, TaxRateCityRef>> FindCitiesAsync(
        IReadOnlyCollection<Guid> cityIds, CancellationToken cancellationToken);
}

/// <summary>El municipio y su departamento, en el vocabulario de Catalog.</summary>
public sealed record TaxRateCityRef(Guid Id, string Name, Guid DepartmentId, string DepartmentName);
