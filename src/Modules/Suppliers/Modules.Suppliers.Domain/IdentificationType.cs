namespace Modules.Suppliers.Domain;

/// <summary>
/// Los tipos de documento que el producto acepta para un proveedor. Mismo catálogo cerrado que
/// el de clientes, copiado a propósito: son dos módulos, y compartirlo los acoplaría por un enum.
/// Viajan al contrato HTTP como NIT, CC, CE y PASAPORTE; la traducción vive en
/// <see cref="IdentificationTypeParser"/>.
/// </summary>
public enum IdentificationType
{
    Nit,
    Cc,
    Ce,
    Pasaporte
}

public static class IdentificationTypeParser
{
    private static readonly Dictionary<string, IdentificationType> ByWireValue =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIT"] = IdentificationType.Nit,
            ["CC"] = IdentificationType.Cc,
            ["CE"] = IdentificationType.Ce,
            ["PASAPORTE"] = IdentificationType.Pasaporte
        };

    /// <summary>La cadena que viaja en el JSON, en mayúsculas.</summary>
    public static string ToWire(this IdentificationType type) => type switch
    {
        IdentificationType.Nit => "NIT",
        IdentificationType.Cc => "CC",
        IdentificationType.Ce => "CE",
        IdentificationType.Pasaporte => "PASAPORTE",
        _ => throw InvalidType()
    };

    /// <summary>
    /// Un valor que no se reconoce **falla**, no cae en un default: elegir NIT en silencio le
    /// cambia el documento al proveedor sin que nadie se entere.
    /// </summary>
    public static IdentificationType Parse(string? value) =>
        value is not null && ByWireValue.TryGetValue(value.Trim(), out var type)
            ? type
            : throw InvalidType();

    public static IReadOnlyCollection<string> SupportedWireValues => ByWireValue.Keys;

    private static SuppliersDomainException InvalidType() => new(
        "suppliers.supplier.identification_type_invalid",
        "The identification type must be one of NIT, CC, CE or PASAPORTE.");
}
