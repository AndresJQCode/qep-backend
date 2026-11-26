namespace Modules.Store.Domain;

/// <summary>
/// Dónde queda una dirección, con las dos carriles de ciudad (spec §4.2): <c>CO</c> lleva
/// departamento y ciudad de DIVIPOLA con sus nombres congelados; cualquier otro país lleva la ciudad
/// tal como la escribió el visitante y ningún id. Un valor que mezcle los carriles no se construye.
/// </summary>
public sealed record StorePlace
{
    public const int NameMaxLength = 120;

    public StorePlace(string country, Guid? departmentId, string? departmentName, Guid? cityId, string cityName)
    {
        // Un país que no son 2 letras no puede estar en ningún carril: se lee como carril roto.
        if (!StoreCountry.IsShapeValid(country?.Trim()))
        {
            throw Lane("El país debe ser un código ISO-3166-1 alpha-2.");
        }

        var normalizedCountry = StoreCountry.Normalize(country!);
        var city = (cityName ?? string.Empty).Trim();
        var department = StoreText.Clean(departmentName);

        if (city.Length == 0 || city.Length > NameMaxLength || department?.Length > NameMaxLength)
        {
            throw Lane("La ciudad es obligatoria y admite hasta 120 caracteres.");
        }

        if (StoreCountry.IsColombia(normalizedCountry))
        {
            if (departmentId is null || departmentId == Guid.Empty
                || cityId is null || cityId == Guid.Empty
                || department is null)
            {
                throw Lane("Una dirección en Colombia lleva departamento y ciudad.");
            }
        }
        else if (departmentId is not null || cityId is not null || department is not null)
        {
            throw Lane("Fuera de Colombia la ciudad se escribe y no lleva ids ni departamento.");
        }

        Country = normalizedCountry;
        DepartmentId = departmentId;
        DepartmentName = department;
        CityId = cityId;
        CityName = city;
    }

    public string Country { get; }

    public Guid? DepartmentId { get; }

    public string? DepartmentName { get; }

    public Guid? CityId { get; }

    public string CityName { get; }

    public bool IsColombian => StoreCountry.IsColombia(Country);

    private static StoreDomainException Lane(string message) => new(StoreErrorCodes.CityInvalid, message);
}
