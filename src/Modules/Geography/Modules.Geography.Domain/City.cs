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

    private City(CityId id, string divipolaCode, string divipolaName, DepartmentId departmentId)
    {
        Id = id;
        DivipolaCode = divipolaCode;
        DivipolaName = divipolaName;
        Name = divipolaName;
        DepartmentId = departmentId;
    }

    public CityId Id { get; private set; }

    public string DivipolaCode { get; private set; } = string.Empty;

    /// <summary>
    /// El nombre que se muestra: el nombre común del municipio cuando hay uno ("CALI"), o el
    /// oficial del DANE (<see cref="DivipolaName"/>) cuando no. Es el que pinta el selector de
    /// ciudad, el PDF y los Excel. Lo fija el importador en cada arranque desde
    /// <c>common-names.json</c> (ver <c>GeographySeeder</c>).
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// El nombre oficial del municipio en DIVIPOLA ("SANTIAGO DE CALI"), tal como lo trae el
    /// archivo del DANE. Se guarda aparte de <see cref="Name"/> para no perderlo cuando el
    /// municipio tiene nombre común, y para que la importación de clientes reconozca los dos.
    /// </summary>
    public string DivipolaName { get; private set; } = string.Empty;

    public DepartmentId DepartmentId { get; private set; }

    /// <summary>
    /// El nombre del municipio tal como lo escribe la transportadora Coordinadora: en mayúscula,
    /// casi siempre sin tildes (la Ñ sí la conserva) y con la abreviatura del departamento entre
    /// paréntesis ("ABEJORRAL (ANT)"). Lo
    /// usa la columna "Ciudad Coordinadora" del Excel de pedidos, que el tenant importa en su ERP
    /// para generar la guía: Coordinadora no reconoce el nombre del DANE ("ABEJORRAL") y no hay
    /// regla que convierta uno en el otro, así que se guarda el suyo. Null cuando Coordinadora no
    /// lista el municipio o lo lista inactivo. Lo fija el importador en cada arranque, desde el
    /// snapshot embebido (ver <c>GeographySeeder</c>).
    /// </summary>
    public string? CoordinadoraName { get; private set; }

    // Nace con el nombre del DANE como nombre oficial y como nombre que se muestra; el nombre
    // común, si lo hay, lo pone después SetCommonName.
    public static City Create(CityId id, string divipolaCode, string divipolaName, DepartmentId departmentId)
    {
        EnsureValidCode(divipolaCode);
        var trimmedName = EnsureValidName(divipolaName);
        return new City(id, divipolaCode, trimmedName, departmentId);
    }

    // Usado por el importador cuando el nombre DANE de un código ya existente cambia de un año de
    // DIVIPOLA al siguiente. Si la ciudad no tiene nombre común, el nombre que se muestra sigue al
    // del DANE; si lo tiene, se conserva.
    public void Rename(string divipolaName)
    {
        var trimmedName = EnsureValidName(divipolaName);
        if (Name == DivipolaName)
        {
            Name = trimmedName;
        }

        DivipolaName = trimmedName;
    }

    // Usado por el importador en cada arranque, después de Rename. Vacío, sólo espacios o null
    // cuenta como "sin nombre común": el nombre que se muestra vuelve al del DANE, así que una
    // ciudad que sale de common-names.json recupera su nombre oficial en el siguiente arranque.
    public void SetCommonName(string? commonName)
    {
        Name = string.IsNullOrWhiteSpace(commonName)
            ? DivipolaName
            : commonName.Trim();
    }

    // Usado por el importador en cada arranque. Vacío o sólo espacios cuenta como "Coordinadora no
    // lo lista": queda null y no una cadena vacía, para que "sin nombre" tenga una sola forma.
    public void SetCoordinadoraName(string? coordinadoraName)
    {
        CoordinadoraName = string.IsNullOrWhiteSpace(coordinadoraName)
            ? null
            : coordinadoraName.Trim();
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
