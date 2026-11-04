using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// Geography → Store: la división DIVIPOLA de Colombia, la única con ids. Traduce al vocabulario de la tienda y deja fuera el código
/// DIVIPOLA, que el visitante no necesita. Geography no tiene tenant, así que nada de esto se acota.
/// </summary>
internal sealed class StoreGeography(IDepartmentRepository departments, ICityRepository cities) : IStoreGeography
{
    // Cultura explícita: el orden no puede depender de la configuración regional del servidor.
    private static readonly StringComparer SpanishOrder =
        StringComparer.Create(new System.Globalization.CultureInfo("es-CO"), ignoreCase: true);

    public async Task<IReadOnlyList<StoreGeoItem>> DepartmentsAsync(CancellationToken cancellationToken) =>
        (await departments.ListAllAsync(cancellationToken))
            .OrderBy(department => department.Name, SpanishOrder)
            .Select(department => new StoreGeoItem(department.Id.Value, department.Name))
            .ToList();

    public async Task<IReadOnlyList<StoreGeoItem>?> CitiesAsync(Guid departmentId, CancellationToken cancellationToken)
    {
        var id = new DepartmentId(departmentId);
        if (await departments.FindAsync(id, cancellationToken) is null)
        {
            return null;
        }

        return (await cities.ListByDepartmentAsync(id, cancellationToken))
            .OrderBy(city => city.Name, SpanishOrder)
            .Select(city => new StoreGeoItem(city.Id.Value, city.Name))
            .ToList();
    }

    public async Task<StoreCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken)
    {
        var city = await cities.FindAsync(new CityId(cityId), cancellationToken);
        if (city is null)
        {
            return null;
        }

        var department = await departments.FindAsync(city.DepartmentId, cancellationToken);
        return department is null
            ? null
            : new StoreCityRef(city.Id.Value, city.Name, department.Id.Value, department.Name);
    }
}
