namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §6.2.1 paso 5 (H10): Shopify entrega el departamento como código ISO 3166-2 sin el prefijo del país
/// (<c>ANT</c>, <c>DC</c>, <c>CUN</c>…) y Geography no guarda ese código, así que la tabla vive acá, en código. Los nombres son
/// <b>exactamente</b> los de la semilla de Geography (<c>Seed/Data/departments.json</c>, DIVIPOLA en mayúsculas y con tildes):
/// <c>ColombiaProvinceCodesGeographyTests</c> lo compara contra la base, así que una semilla que cambie de nombre rompe esa prueba
/// y no la importación en silencio. Que Shopify use estos mismos códigos para Colombia está por confirmar con una tienda real (V-4).
/// </summary>
public static class ColombiaProvinceCodes
{
    /// <summary>Los 33: 32 departamentos y Bogotá, D.C.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AMA"] = "AMAZONAS",
        ["ANT"] = "ANTIOQUIA",
        ["ARA"] = "ARAUCA",
        ["ATL"] = "ATLÁNTICO",
        ["BOL"] = "BOLÍVAR",
        ["BOY"] = "BOYACÁ",
        ["CAL"] = "CALDAS",
        ["CAQ"] = "CAQUETÁ",
        ["CAS"] = "CASANARE",
        ["CAU"] = "CAUCA",
        ["CES"] = "CESAR",
        ["CHO"] = "CHOCÓ",
        ["COR"] = "CÓRDOBA",
        ["CUN"] = "CUNDINAMARCA",
        ["DC"] = "BOGOTÁ, D.C.",
        ["GUA"] = "GUAINÍA",
        ["GUV"] = "GUAVIARE",
        ["HUI"] = "HUILA",
        ["LAG"] = "LA GUAJIRA",
        ["MAG"] = "MAGDALENA",
        ["MET"] = "META",
        ["NAR"] = "NARIÑO",
        ["NSA"] = "NORTE DE SANTANDER",
        ["PUT"] = "PUTUMAYO",
        ["QUI"] = "QUINDÍO",
        ["RIS"] = "RISARALDA",
        ["SAN"] = "SANTANDER",
        ["SAP"] = "ARCHIPIÉLAGO DE SAN ANDRÉS, PROVIDENCIA Y SANTA CATALINA",
        ["SUC"] = "SUCRE",
        ["TOL"] = "TOLIMA",
        ["VAC"] = "VALLE DEL CAUCA",
        ["VAU"] = "VAUPÉS",
        ["VID"] = "VICHADA",
    };

    private const string CountryPrefix = "CO-";

    /// <summary>El nombre del departamento en Geography, o <c>null</c> si el código no es de Colombia. Acepta minúsculas,
    /// espacios y el código completo con <c>CO-</c>.</summary>
    public static string? DepartmentNameFor(string code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.StartsWith(CountryPrefix, StringComparison.Ordinal))
        {
            normalized = normalized[CountryPrefix.Length..];
        }

        return All.GetValueOrDefault(normalized);
    }
}
