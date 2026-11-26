using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Geography → Support: la división DIVIPOLA de Colombia para el formulario público (plan F19), calco de <see cref="StoreGeography"/>.
/// Deja fuera el código DIVIPOLA, que el visitante no necesita. Geography no tiene tenant, así que nada de esto se acota.
/// </summary>
internal sealed class SupportGeography(IDepartmentRepository departments, ICityRepository cities) : ISupportGeography
{
    // Cultura explícita: el orden no puede depender de la configuración regional del servidor.
    private static readonly StringComparer SpanishOrder =
        StringComparer.Create(new System.Globalization.CultureInfo("es-CO"), ignoreCase: true);

    public async Task<IReadOnlyList<SupportGeoItem>> DepartmentsAsync(CancellationToken cancellationToken) =>
        (await departments.ListAllAsync(cancellationToken))
            .OrderBy(department => department.Name, SpanishOrder)
            .Select(department => new SupportGeoItem(department.Id.Value, department.Name))
            .ToList();

    public async Task<IReadOnlyList<SupportGeoItem>?> CitiesAsync(Guid departmentId, CancellationToken cancellationToken)
    {
        var id = new DepartmentId(departmentId);
        if (await departments.FindAsync(id, cancellationToken) is null)
        {
            return null;
        }

        return (await cities.ListByDepartmentAsync(id, cancellationToken))
            .OrderBy(city => city.Name, SpanishOrder)
            .Select(city => new SupportGeoItem(city.Id.Value, city.Name))
            .ToList();
    }

    public async Task<SupportGeoItem?> FindCityAsync(Guid cityId, CancellationToken cancellationToken)
    {
        var city = await cities.FindAsync(new CityId(cityId), cancellationToken);
        return city is null ? null : new SupportGeoItem(city.Id.Value, city.Name);
    }
}
