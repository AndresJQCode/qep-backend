using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de ciudades de <c>Geography</c> al puerto que <c>wholesale</c> declara, igual que
/// <c>CompanyGeographyLookup</c> lo hace para <c>companies</c>: vive en el composition root porque ningún módulo de negocio
/// referencia al otro. No decide nada: devuelve la ciudad con su departamento, y la regla de que la ciudad exista al invitar
/// es del handler de Wholesale. Un departamento que no se encuentre no vuelve inexistente la ciudad: sale sin su nombre.
/// </summary>
internal sealed class WholesaleCityLookup(
    ICityRepository cityRepository,
    IDepartmentRepository departmentRepository) : IWholesaleCityLookup
{
    public async Task<WholesaleCityRef?> FindAsync(Guid cityId, CancellationToken cancellationToken)
    {
        var city = await cityRepository.FindAsync(new CityId(cityId), cancellationToken);
        if (city is null)
        {
            return null;
        }

        var department = await departmentRepository.FindAsync(city.DepartmentId, cancellationToken);
        return new WholesaleCityRef(city.Id.Value, city.Name, city.DepartmentId.Value, department?.Name);
    }
}
