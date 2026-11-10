using Modules.Store.Domain;

namespace Modules.Store.Application;

/// <summary>
/// Del bloque de facturación y el de envío del checkout a lo que piden los vecinos: el cliente (Customers), su libreta y las partes del
/// pedido (Quotations). Una ciudad de Colombia va sólo con su id; afuera, sólo con el texto.
/// </summary>
internal static class StoreOrderRequestParties
{
    public static StoreNewCustomer BillingData(StoreRequestBilling billing, Guid? classificationId)
    {
        ArgumentNullException.ThrowIfNull(billing);

        var place = billing.Place;
        return new StoreNewCustomer(
            billing.Name,
            billing.IdentificationType,
            billing.IdentificationNumber,
            billing.Phone,
            billing.Email,
            billing.Address,
            place.Country,
            place.CityId,
            place.IsColombian ? null : place.CityName,
            classificationId);
    }

    /// <summary>La de facturación y, si vino, la de envío, en ese orden; el teléfono va en E.164.</summary>
    public static List<StoreAddressInput> AddressInputs(StoreRequestBilling billing, StoreRequestShipping? shipping)
    {
        ArgumentNullException.ThrowIfNull(billing);

        var inputs = new List<StoreAddressInput>
        {
            new(billing.Name, billing.Address, billing.PhoneE164, billing.Place.Country, billing.Place.CityId,
                billing.Place.IsColombian ? null : billing.Place.CityName),
        };
        if (shipping is not null)
        {
            inputs.Add(new StoreAddressInput(
                shipping.EffectiveRecipientName(billing), shipping.Address, shipping.PhoneE164, shipping.Place.Country,
                shipping.Place.CityId, shipping.Place.IsColombian ? null : shipping.Place.CityName));
        }

        return inputs;
    }

    public static StoreOrderPartyInput BillingParty(StoreRequestBilling billing)
    {
        ArgumentNullException.ThrowIfNull(billing);

        return new StoreOrderPartyInput(
            billing.Name,
            billing.Phone,
            billing.Email,
            billing.Address,
            billing.Place.Country,
            billing.Place.DepartmentId,
            billing.Place.CityId,
            billing.Place.IsColombian ? null : billing.Place.CityName,
            billing.IdentificationType,
            billing.IdentificationNumber);
    }

    /// <summary>
    /// Sin «Usar los mismos datos» el destinatario es quien el visitante escribió; con él, la parte repite la facturación. En los dos
    /// casos lleva el correo de facturación (el bloque de envío no tiene uno) y ningún documento: éste va sólo en facturación.
    /// </summary>
    public static StoreOrderPartyInput ShippingParty(StoreRequestBilling billing, StoreRequestShipping? shipping)
    {
        if (shipping is null)
        {
            return BillingParty(billing) with { IdentificationType = null, IdentificationNumber = null };
        }

        ArgumentNullException.ThrowIfNull(billing);
        return new StoreOrderPartyInput(
            shipping.EffectiveRecipientName(billing),
            shipping.Phone,
            billing.Email,
            shipping.Address,
            shipping.Place.Country,
            shipping.Place.DepartmentId,
            shipping.Place.CityId,
            shipping.Place.IsColombian ? null : shipping.Place.CityName,
            null,
            null);
    }
}
