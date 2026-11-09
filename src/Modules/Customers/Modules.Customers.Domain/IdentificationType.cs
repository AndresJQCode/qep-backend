namespace Modules.Customers.Domain;

/// <summary>
/// Los siete códigos de documento que el producto conoce. Qué país acepta cuáles lo decide
/// <c>DocumentTypesByCountry</c> (BuildingBlocks.Domain), no este enum.
///
/// Enum cerrado y no texto libre porque el formulario ya los ofrece como lista cerrada
/// (<c>IDENTIFICATION_TYPES</c> en <c>customer-form.schema.ts</c>) y porque la identificacion es
/// la clave de unicidad del cliente: con texto libre, "NIT" y "Nit" son dos documentos distintos
/// y el indice unico deja pasar el duplicado que existe para frenar.
///
/// Los nombres viajan al contrato HTTP como las cadenas que el frontend ya manda —NIT, CC, CE,
/// PASAPORTE, NIF, NIE, TAX_ID—; la traduccion vive en <see cref="IdentificationTypeParser"/>.
/// </summary>
public enum IdentificationType
{
    Nit,
    Cc,
    Ce,
    Pasaporte,
    Nif,
    Nie,
    TaxId
}

public static class IdentificationTypeParser
{
    private static readonly Dictionary<string, IdentificationType> ByWireValue =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIT"] = IdentificationType.Nit,
            ["CC"] = IdentificationType.Cc,
            ["CE"] = IdentificationType.Ce,
            ["PASAPORTE"] = IdentificationType.Pasaporte,
            ["NIF"] = IdentificationType.Nif,
            ["NIE"] = IdentificationType.Nie,
            ["TAX_ID"] = IdentificationType.TaxId
        };

    /// <summary>La cadena que viaja en el JSON, en mayusculas como la manda el formulario.</summary>
    public static string ToWireValue(this IdentificationType type) => type switch
    {
        IdentificationType.Nit => "NIT",
        IdentificationType.Cc => "CC",
        IdentificationType.Ce => "CE",
        IdentificationType.Pasaporte => "PASAPORTE",
        IdentificationType.Nif => "NIF",
        IdentificationType.Nie => "NIE",
        IdentificationType.TaxId => "TAX_ID",
        _ => throw new CustomersDomainException(
            "customers.customer.identification_type_invalid",
            "The identification type is not one of the supported values.")
    };

    /// <summary>
    /// Un valor que no se reconoce **falla**, no cae en un default. Elegir NIT en silencio le
    /// cambia el documento al cliente sin que nadie se entere, y la identificacion es lo que lo
    /// identifica.
    /// </summary>
    public static IdentificationType Parse(string? value)
    {
        if (value is not null && ByWireValue.TryGetValue(value.Trim(), out var type))
        {
            return type;
        }

        throw new CustomersDomainException(
            "customers.customer.identification_type_invalid",
            $"The identification type must be one of {string.Join(", ", SupportedWireValues)}.");
    }

    public static IReadOnlyCollection<string> SupportedWireValues => ByWireValue.Keys;
}
