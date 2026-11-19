namespace Modules.Suppliers.Domain;

/// <summary>
/// Los tipos de documento que el producto acepta para un proveedor. Mismo catálogo cerrado que
/// el de clientes, copiado a propósito: son dos módulos, y compartirlo los acoplaría por un enum.
/// Viajan al contrato HTTP como NIT, CC, CE, PASAPORTE, NIF, NIE y TAX_ID; la traducción vive en
/// <see cref="IdentificationTypeParser"/>.
/// Son los siete porque un mayorista puede ser de cualquier país: el proveedor vinculado nace del
/// documento de su empresa facturadora, y uno de España (NIF) o de otro país (TAX_ID) tiene que poder invitar distribuidores.
/// Qué país acepta cuáles no se juzga acá: es del formulario de la empresa o del cliente, no del proveedor.
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

    /// <summary>La cadena que viaja en el JSON, en mayúsculas.</summary>
    public static string ToWire(this IdentificationType type) => type switch
    {
        IdentificationType.Nit => "NIT",
        IdentificationType.Cc => "CC",
        IdentificationType.Ce => "CE",
        IdentificationType.Pasaporte => "PASAPORTE",
        IdentificationType.Nif => "NIF",
        IdentificationType.Nie => "NIE",
        IdentificationType.TaxId => "TAX_ID",
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
        "The identification type must be one of NIT, CC, CE, PASAPORTE, NIF, NIE or TAX_ID.");
}
