using Modules.Geography.Application;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §6.2.1 paso 5 (H10): la ciudad de la dirección de envío, por nombre dentro de su departamento.
/// Geography es global (DIVIPOLA), sin tenant.</summary>
internal sealed class ShopifyGeographyLookup(IDepartmentRepository departments, ICityRepository cities) : IShopifyGeographyLookup
{
    public async Task<ShopifyCityRef?> FindCityAsync(string departmentName, string cityName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(departmentName) || string.IsNullOrWhiteSpace(cityName))
        {
            return null;
        }

        var department = await departments.FindByNameAsync(departmentName.Trim(), cancellationToken);
        if (department is null)
        {
            return null;
        }

        var city = await cities.FindByNameAsync(department.Id, cityName.Trim(), cancellationToken);
        return city is null ? null : new ShopifyCityRef(department.Id.Value, city.Id.Value);
    }
}
