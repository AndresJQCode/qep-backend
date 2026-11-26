namespace BuildingBlocks.Domain.Identification;

/// <summary>
/// Catálogo compartido de tipos de documento por país. Es la misma tabla que
/// <c>CompanyDocumentTypes.AllowedFor</c> de Companies; el test de paridad vive en
/// <c>Modules.Companies.UnitTests</c> y las ata. Los códigos son los del wire.
/// </summary>
public static class DocumentTypesByCountry
{
    private static readonly string[] Colombia = ["NIT", "CC", "CE", "PASAPORTE"];
    private static readonly string[] Spain = ["NIF", "NIE", "PASAPORTE"];
    private static readonly string[] Other = ["TAX_ID", "PASAPORTE"];

    /// <summary>Los siete códigos, en orden estable.</summary>
    public static IReadOnlyList<string> All { get; } = ["NIT", "CC", "CE", "PASAPORTE", "NIF", "NIE", "TAX_ID"];

    /// <summary>
    /// Códigos permitidos para el país, en orden fijo (es una lista: la plantilla de importación
    /// la muestra como desplegable). El país se recorta y se pasa a mayúsculas.
    /// </summary>
    public static IReadOnlyList<string> AllowedFor(string country) =>
        country.Trim().ToUpperInvariant() switch
        {
            "CO" => Colombia,
            "ES" => Spain,
            _ => Other,
        };

    /// <summary>Si el código es uno de los siete, sin importar mayúsculas ni espacios.</summary>
    public static bool IsKnown(string? type) =>
        type is not null && All.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Si el código es válido para el país.</summary>
    public static bool IsAllowed(string? type, string country) =>
        type is not null && AllowedFor(country).Contains(type.Trim(), StringComparer.OrdinalIgnoreCase);
}
