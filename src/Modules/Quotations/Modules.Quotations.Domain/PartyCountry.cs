namespace Modules.Quotations.Domain;

/// <summary>
/// La regla de lectura del país de la facturación (spec 2026-11-27), en un solo lugar para
/// <see cref="QuotationParty"/> y <see cref="OrderParty"/>. Antes el <c>CO</c> se guardaba como <c>null</c>; desde ese
/// cambio la facturación guarda <c>CO</c> literal, así que un <c>null</c> viejo se interpreta: con ciudad de DIVIPOLA
/// es Colombia (el carril sólo admite esos ids en Colombia) y sin ciudad es «pendiente de país».
/// </summary>
internal static class PartyCountry
{
    /// <summary>El país que se lee de la parte: el guardado, salvo una facturación vieja con ciudad, que es Colombia.</summary>
    public static string? Effective(QuotationPartyRole role, string? country, Guid? cityId) =>
        role == QuotationPartyRole.Billing && country is null && cityId is not null
            ? QuotationPartyDetails.ColombiaCountryCode
            : country;

    /// <summary>Facturación con nombre, sin país y sin ciudad: no se sabe a qué país factura.</summary>
    public static bool IsPending(QuotationPartyRole role, string? name, string? country, Guid? cityId) =>
        role == QuotationPartyRole.Billing
        && !string.IsNullOrWhiteSpace(name)
        && country is null
        && cityId is null;
}
