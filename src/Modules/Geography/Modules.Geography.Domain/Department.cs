namespace Modules.Geography.Domain;

/// <summary>
/// Un departamento de la división político-administrativa de Colombia (DIVIPOLA). Dato de
/// referencia global, sin tenant: lo importa <c>GeographySeeder</c> desde el archivo oficial del
/// DANE y no lo crea ningún caso de uso de escritura.
/// </summary>
public sealed class Department
{
    private Department()
    {
    }

    private Department(DepartmentId id, string divipolaCode, string name)
    {
        Id = id;
        DivipolaCode = divipolaCode;
        Name = name;
    }

    public DepartmentId Id { get; private set; }

    public string DivipolaCode { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public static Department Create(DepartmentId id, string divipolaCode, string name)
    {
        EnsureValidCode(divipolaCode);
        var trimmedName = EnsureValidName(name);
        return new Department(id, divipolaCode, trimmedName);
    }

    // Usado por el importador cuando el nombre de un código ya existente cambia de un año de
    // DIVIPOLA al siguiente (ej. un municipio renombrado).
    public void Rename(string name)
    {
        Name = EnsureValidName(name);
    }

    private static void EnsureValidCode(string divipolaCode)
    {
        if (divipolaCode is not { Length: 2 } || !divipolaCode.All(char.IsAsciiDigit))
        {
            throw new GeographyDomainException(
                "geography.department.code_invalid",
                "The department DIVIPOLA code must be exactly 2 digits.");
        }
    }

    private static string EnsureValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new GeographyDomainException(
                "geography.department.name_required",
                "The department name is required.");
        }

        return name.Trim();
    }
}
