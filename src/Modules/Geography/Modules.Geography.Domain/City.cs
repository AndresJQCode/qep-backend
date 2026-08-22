namespace Modules.Geography.Domain;

/// <summary>
/// Una ciudad de la división político-administrativa de Colombia (DIVIPOLA), anidada bajo un
/// departamento. Dato de referencia global, sin tenant. Cubre los dos niveles que trae el archivo
/// fuente del DANE bajo el mismo código: municipios (código de 5 dígitos) y centros
/// poblados/corregimientos (código de 8 dígitos, con el municipio como sus primeros 5 dígitos).
/// </summary>
public sealed class City
{
    private City()
    {
    }

    private City(CityId id, string divipolaCode, string name, DepartmentId departmentId)
    {
        Id = id;
        DivipolaCode = divipolaCode;
        Name = name;
        DepartmentId = departmentId;
    }

    public CityId Id { get; private set; }

    public string DivipolaCode { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public DepartmentId DepartmentId { get; private set; }

    public static City Create(CityId id, string divipolaCode, string name, DepartmentId departmentId)
    {
        EnsureValidCode(divipolaCode);
        var trimmedName = EnsureValidName(name);
        return new City(id, divipolaCode, trimmedName, departmentId);
    }

    // Usado por el importador cuando el nombre de un código ya existente cambia de un año de
    // DIVIPOLA al siguiente.
    public void Rename(string name)
    {
        Name = EnsureValidName(name);
    }

    private static void EnsureValidCode(string divipolaCode)
    {
        if (divipolaCode is not { Length: 5 or 8 } || !divipolaCode.All(char.IsAsciiDigit))
        {
            throw new GeographyDomainException(
                "geography.city.code_invalid",
                "The city DIVIPOLA code must be exactly 5 or 8 digits.");
        }
    }

    private static string EnsureValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new GeographyDomainException(
                "geography.city.name_required",
                "The city name is required.");
        }

        return name.Trim();
    }
}
