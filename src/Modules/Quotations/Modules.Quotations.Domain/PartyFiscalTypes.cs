namespace Modules.Quotations.Domain;

/// <summary>
/// Natural o jurídica, para la parte de facturación (spec 2026-10-26). Duplica
/// <c>PersonType</c> de Customers porque Quotations no referencia ese módulo (ArchitectureTests);
/// si allá cambian los valores, cambian acá también.
/// </summary>
public enum PartyPersonType
{
    Natural,
    Juridica
}

/// <summary>
/// Tipo de documento de la parte de facturación: los siete códigos del producto (NIT, CC, CE, PASAPORTE,
/// NIF, NIE, TAX_ID). Qué país acepta cuáles lo decide <c>DocumentTypesByCountry</c> (BuildingBlocks.Domain),
/// no este enum. Duplica el <c>IdentificationType</c> de Customers por la misma razón que
/// <see cref="PartyPersonType"/>.
/// </summary>
public enum PartyIdentificationType
{
    Nit,
    Cc,
    Ce,
    Pasaporte,
    Nif,
    Nie,
    TaxId
}

/// <summary>
/// Traduce los dos enums al texto del contrato HTTP. Duplica <c>IdentificationTypeParser</c> y su
/// equivalente de <c>PersonType</c> en Customers porque Quotations no referencia ese módulo.
/// </summary>
public static class PartyFiscalTypesParser
{
    private static readonly Dictionary<string, PartyPersonType> PersonTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Natural"] = PartyPersonType.Natural,
            ["Juridica"] = PartyPersonType.Juridica
        };

    private static readonly Dictionary<string, PartyIdentificationType> IdentificationTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NIT"] = PartyIdentificationType.Nit,
            ["CC"] = PartyIdentificationType.Cc,
            ["CE"] = PartyIdentificationType.Ce,
            ["PASAPORTE"] = PartyIdentificationType.Pasaporte,
            ["NIF"] = PartyIdentificationType.Nif,
            ["NIE"] = PartyIdentificationType.Nie,
            ["TAX_ID"] = PartyIdentificationType.TaxId
        };

    public static string ToWireValue(this PartyPersonType type) => type switch
    {
        PartyPersonType.Natural => "Natural",
        PartyPersonType.Juridica => "Juridica",
        _ => throw new QuotationsDomainException(
            "quotations.party.person_type_invalid",
            "The person type is not one of the supported values.")
    };

    public static string ToWireValue(this PartyIdentificationType type) => type switch
    {
        PartyIdentificationType.Nit => "NIT",
        PartyIdentificationType.Cc => "CC",
        PartyIdentificationType.Ce => "CE",
        PartyIdentificationType.Pasaporte => "PASAPORTE",
        PartyIdentificationType.Nif => "NIF",
        PartyIdentificationType.Nie => "NIE",
        PartyIdentificationType.TaxId => "TAX_ID",
        _ => throw new QuotationsDomainException(
            "quotations.party.identification_type_invalid",
            "The identification type is not one of the supported values.")
    };

    /// <summary>Blanco es "sin elegir" (<c>null</c>); un valor que no se reconoce falla, no cae en
    /// un default.</summary>
    public static PartyPersonType? ParsePersonType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return PersonTypes.TryGetValue(value.Trim(), out var type)
            ? type
            : throw new QuotationsDomainException(
                "quotations.party.person_type_invalid",
                "The person type must be one of Natural or Juridica.");
    }

    public static PartyIdentificationType? ParseIdentificationType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return IdentificationTypes.TryGetValue(value.Trim(), out var type)
            ? type
            : throw new QuotationsDomainException(
                "quotations.party.identification_type_invalid",
                $"The identification type must be one of {string.Join(", ", SupportedIdentificationTypes)}.");
    }

    public static IReadOnlyCollection<string> SupportedPersonTypes => PersonTypes.Keys;

    public static IReadOnlyCollection<string> SupportedIdentificationTypes => IdentificationTypes.Keys;
}
