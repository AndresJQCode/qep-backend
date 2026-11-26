using BuildingBlocks.Domain.Taxes;

namespace Modules.Companies.Application;

/// <summary>
/// Una tasa del catálogo de impuestos tal como Companies la necesita para validar y pintar las tasas municipales de la
/// empresa (spec ReteICA por empresa §7.3). <c>Type</c> viaja como texto; <c>CityId</c> es el municipio de la tasa (null en
/// una nacional). <c>AppliesTo</c> es su alcance (spec 2026-11-18 §4.3): entra por este puerto, como <c>Type</c>, porque la
/// tasa no lo cambia después de crearse y la validación por id es estable.
/// </summary>
public sealed record CompanyTaxRateRef(
    Guid Id,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    bool IsActive,
    bool Withholds,
    Guid? CityId,
    WithholdingScope AppliesTo = WithholdingScope.All);

/// <summary>
/// Puerto de Companies hacia el catálogo de impuestos. Companies nunca referencia Catalog: el adaptador
/// (<c>CompanyTaxRateLookup</c>) vive en Bootstrapper. Sólo ve las tasas del tenant, así que una tasa ajena se comporta
/// como una que no existe.
/// </summary>
public interface ICompanyTaxRateLookup
{
    /// <summary>Las tasas pedidas que existen en el tenant, por id. Las que faltan no están en el resultado.</summary>
    Task<IReadOnlyDictionary<Guid, CompanyTaxRateRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken);
}
