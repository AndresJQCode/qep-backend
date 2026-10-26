using Modules.Geography.Application;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §6.2.1 paso 5 (H10): la ciudad de la dirección de envío, por nombre dentro de su departamento.
/// Geography es global (DIVIPOLA), sin tenant. Spec 2026-10-26: casa también por sinónimo y dice por cuál (D-C13), y devuelve el
/// departamento aunque la ciudad no case (D-C11).</summary>
internal sealed class ShopifyGeographyLookup(IDepartmentRepository departments, ICityRepository cities) : IShopifyGeographyLookup
{
    private static readonly ShopifyCityLookup Nothing = new(null, null);

    public async Task<ShopifyCityLookup> LookupCityAsync(string departmentName, string cityName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(departmentName))
        {
            return Nothing;
        }

        var department = await departments.FindByNameAsync(departmentName.Trim(), cancellationToken);
        if (department is null)
        {
            return Nothing;
        }

        var match = string.IsNullOrWhiteSpace(cityName)
            ? null
            : await cities.MatchByNameAsync(department.Id, cityName.Trim(), cancellationToken);
        return new ShopifyCityLookup(
            department.Id.Value,
            match is null ? null : new ShopifyCityRef(department.Id.Value, match.City.Id.Value, match.SynonymId?.Value));
    }
}
