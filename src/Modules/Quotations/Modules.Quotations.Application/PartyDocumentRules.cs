using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Condición compartida de la regla "tipo de documento permitido para el país" de los validadores de
/// partes. Corre sólo con un código conocido y un país en blanco (Colombia) o con forma ISO alfa-2: así un
/// país mal escrito se informa en el país y un código desconocido en el tipo, cada uno con su error.
/// </summary>
internal static class PartyDocumentRules
{
    public static bool IsKnownTypeWithAValidCountry(string? type, string? country) =>
        PartyFiscalTypesParser.SupportedIdentificationTypes
            .Contains(type?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(country)
            || country.Trim() is { Length: QuotationPartyDetails.CountryLength } code && code.All(char.IsAsciiLetter));
}
