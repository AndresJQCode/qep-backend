namespace Modules.Companies.Domain;

/// <summary>
/// Los tipos de documento que el producto acepta para una empresa. Viajan al contrato HTTP en
/// mayúsculas y en español (NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID); la traducción vive en
/// <see cref="CompanyDocumentTypes"/>. <see cref="TaxId"/> es la «Identificación tributaria»
/// genérica, para países sin un documento propio en el catálogo.
/// </summary>
public enum CompanyDocumentType
{
    Nit,
    Cc,
    Ce,
    Pasaporte,
    Nif,
    Nie,
    TaxId
}

public static class CompanyDocumentTypes
{
    private static readonly Dictionary<string, CompanyDocumentType> ByWireValue =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIT"] = CompanyDocumentType.Nit,
            ["CC"] = CompanyDocumentType.Cc,
            ["CE"] = CompanyDocumentType.Ce,
            ["PASAPORTE"] = CompanyDocumentType.Pasaporte,
            ["NIF"] = CompanyDocumentType.Nif,
            ["NIE"] = CompanyDocumentType.Nie,
            ["TAX_ID"] = CompanyDocumentType.TaxId
        };

    private static readonly IReadOnlySet<CompanyDocumentType> Colombia = new HashSet<CompanyDocumentType>
    {
        CompanyDocumentType.Nit, CompanyDocumentType.Cc, CompanyDocumentType.Ce, CompanyDocumentType.Pasaporte
    };

    private static readonly IReadOnlySet<CompanyDocumentType> Spain = new HashSet<CompanyDocumentType>
    {
        CompanyDocumentType.Nif, CompanyDocumentType.Nie, CompanyDocumentType.Pasaporte
    };

    private static readonly IReadOnlySet<CompanyDocumentType> Other = new HashSet<CompanyDocumentType>
    {
        CompanyDocumentType.TaxId, CompanyDocumentType.Pasaporte
    };

    /// <summary>La cadena que viaja en el JSON, en mayúsculas.</summary>
    public static string ToWire(this CompanyDocumentType type) => type switch
    {
        CompanyDocumentType.Nit => "NIT",
        CompanyDocumentType.Cc => "CC",
        CompanyDocumentType.Ce => "CE",
        CompanyDocumentType.Pasaporte => "PASAPORTE",
        CompanyDocumentType.Nif => "NIF",
        CompanyDocumentType.Nie => "NIE",
        CompanyDocumentType.TaxId => "TAX_ID",
        _ => throw InvalidType()
    };

    /// <summary>
    /// Un valor que no se reconoce **falla**, no cae en un default: elegir uno en silencio le
    /// cambia el documento a la empresa sin que nadie se entere.
    /// </summary>
    public static CompanyDocumentType Parse(string wire) =>
        TryParse(wire, out var type) ? type : throw InvalidType();

    /// <summary>Ignora mayúsculas y espacios en los bordes; un valor vacío o nulo no se reconoce.</summary>
    public static bool TryParse(string? wire, out CompanyDocumentType type)
    {
        type = default;
        return wire is not null && ByWireValue.TryGetValue(wire.Trim(), out type);
    }

    /// <summary>
    /// Los tipos de documento que admite un país (código ISO alfa-2). Es la única definición de
    /// D2 en el backend: Colombia, España y, para el resto, identificación tributaria o pasaporte.
    /// Recorta y pasa a mayúsculas el país que recibes, así que no hace falta normalizarlo antes.
    /// </summary>
    public static IReadOnlySet<CompanyDocumentType> AllowedFor(string country) =>
        country.Trim().ToUpperInvariant() switch
        {
            "CO" => Colombia,
            "ES" => Spain,
            _ => Other
        };

    private static CompaniesDomainException InvalidType() => new(
        "companies.company.document_type_invalid",
        "The document type must be one of NIT, CC, CE, PASAPORTE, NIF, NIE or TAX_ID.");
}
