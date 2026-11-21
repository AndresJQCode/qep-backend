namespace Modules.Geography.Domain;

/// <summary>
/// Un país del catálogo de referencia (ISO 3166-1 alfa-2). Dato global, sin tenant: lo siembra
/// <c>GeographySeeder</c> desde <c>countries.json</c> y no lo crea ningún caso de uso de escritura.
/// La clave es el propio código, que es lo único que viaja entre módulos.
/// </summary>
public sealed class Country
{
    private Country()
    {
    }

    private Country(string code, string name)
    {
        Code = code;
        Name = name;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public static Country Create(string code, string name)
    {
        EnsureValidCode(code);
        return new Country(code, EnsureValidName(name));
    }

    // Usado por el importador cuando el nombre de un código ya existente cambia en el archivo.
    public void Rename(string name)
    {
        Name = EnsureValidName(name);
    }

    private static void EnsureValidCode(string code)
    {
        if (code is not { Length: 2 } || !code.All(char.IsAsciiLetterUpper))
        {
            throw new GeographyDomainException(
                "geography.country.code_invalid",
                "The country code must be exactly 2 uppercase ASCII letters (ISO 3166-1 alpha-2).");
        }
    }

    private static string EnsureValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new GeographyDomainException(
                "geography.country.name_required",
                "The country name is required.");
        }

        return name.Trim();
    }
}
