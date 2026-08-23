namespace Modules.Geography.Domain;

/// <summary>
/// Una ciudad de la división político-administrativa de Colombia (DIVIPOLA), anidada bajo un
/// departamento. Dato de referencia global, sin tenant. Sólo el nivel municipio (código de 5
/// dígitos): el archivo fuente del DANE también trae centros poblados/corregimientos (código de
/// 8 dígitos) anidados bajo cada municipio, pero comparten nombre con su municipio y con otros
/// centros poblados de todo el país (p.ej. "SAN ANTONIO" repite más de 30 veces) — dentro de un
/// mismo departamento el nombre de un municipio es único, el de un centro poblado no. Excluidos
/// a propósito para que el selector de ciudad de un formulario no muestre nombres repetidos.
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
        if (divipolaCode is not { Length: 5 } || !divipolaCode.All(char.IsAsciiDigit))
        {
            throw new GeographyDomainException(
                "geography.city.code_invalid",
                "The city DIVIPOLA code must be exactly 5 digits.");
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
