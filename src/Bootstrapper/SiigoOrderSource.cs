using Modules.Customers.Application;
using Modules.Quotations.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="ISiigoOrderSource"/> (spec 2026-11-18 §5.5) sobre <see cref="IExternalOrderInvoicing"/> de Quotations. Vive
/// en Bootstrapper porque Siigo no referencia a Quotations (<c>SiigoLayerTests</c>): traduce los enums de documento y de persona <b>por
/// nombre</b> (<c>SiigoOrderSourceParityTests</c> amarra los dos juegos), la ciudad a códigos DANE con <see cref="ISiigoGeographyLookup"/>
/// y la cuenta de cobro a la clave con que Siigo vincula la forma de pago (<see cref="BankAccountKey"/>). El teléfono de la parte
/// facturada se parte acá en indicativo y número con <see cref="IPhoneNumberSplitter"/> (spec 2026-11-25), porque Siigo no referencia a
/// Customers.
/// </summary>
internal sealed class SiigoOrderSource(IExternalOrderInvoicing invoicing, ISiigoGeographyLookup geography, IPhoneNumberSplitter phones)
    : ISiigoOrderSource
{
    /// <summary>El proveedor con que se marca el pedido: columna <c>quotations.orders.invoice_provider</c>.</summary>
    public const string Provider = "siigo";

    public async Task<SiigoInvoiceInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await invoicing.GetForInvoicingAsync(tenantId, orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var party = order.BilledParty;
        var place = await PlaceOfAsync(party, cancellationToken);
        return new SiigoInvoiceInput(
            order.OrderId,
            order.OrderNumber,
            order.Currency,
            order.Today,
            order.CashOnDelivery,
            [.. order.Lines.Select(line => new SiigoInvoiceLineInput(
                line.VariantId, line.Name, line.Quantity, line.Subtotal, line.DiscountPercentage, line.TaxPercentage, line.TaxAmount,
                line.IsGift, line.ListUnitPrice))],
            order.FreightSubtotal,
            order.FreightTaxAmount,
            order.FreightTaxPercentage,
            order.ProductsTotal,
            order.Total,
            order.RetentionAmount,
            order.NetTotal,
            [.. order.Withholdings.Select(value => new SiigoWithholdingInput(value.TaxRateId, value.Name, value.Type, value.Amount, value.Percentage))],
            [.. order.PaymentProofs.Select(proof => new SiigoPaymentProofInput(proof.ProofId, proof.Amount))],
            BankAccountKey.Normalize(order.BillingAccountBankName, order.BillingAccountNumber),
            order.BuyerNotes,
            new SiigoBilledPartyInput(
                party.IsFinalConsumer,
                Translate<SiigoPersonKind>(party.PersonType),
                Translate<SiigoIdentificationKind>(party.IdentificationType),
                party.Identification,
                party.Name,
                party.Address,
                place.CityCode,
                place.StateCode,
                party.Email,
                PhoneOf(party),
                place.SiigoCountryCode,
                place.UnlinkedCity,
                party.CountryPending),
            order.CompanyId,
            order.Version,
            order.AdvisorMembershipId,
            order.AdvisorLabel,
            order.ShippingAddress,
            order.ShippingCity);
    }

    public Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        invoicing.GetVersionAsync(tenantId, orderId, cancellationToken);

    public Task MarkInvoicedAsync(
        Guid tenantId, Guid orderId, string number, string siigoInvoiceId, Guid memberId, CancellationToken cancellationToken) =>
        invoicing.MarkInvoicedAsync(tenantId, orderId, Provider, number, siigoInvoiceId, memberId, cancellationToken);

    public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken) =>
        invoicing.IsMarkedAsync(tenantId, orderId, Provider, number, cancellationToken);

    public Task<bool> UnmarkInvoicedAsync(
        Guid tenantId, Guid orderId, string number, Guid memberId, CancellationToken cancellationToken) =>
        invoicing.RevertExternalInvoicingAsync(tenantId, orderId, Provider, number, memberId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
        (await invoicing.ListLabelsAsync(tenantId, orderIds, cancellationToken)).ToDictionary(
            label => label.Key, label => new SiigoOrderLabel(label.Value.OrderNumber, label.Value.CustomerName));

    // Dónde se factura. Colombia: los códigos DANE de la ciudad por id. Otro país con ciudad escrita (spec 2026-11-26): se pregunta a la
    // lista de Siigo (sólo lectura; anotar en la bandeja es de SiigoForeignCityInbox, que llaman la vista previa y la emisión). El país va
    // recortado y en mayúscula, como lo guarda el catálogo. Otro país sin ciudad escrita: nada, y el armador dice customer_city_missing.
    private async Task<Place> PlaceOfAsync(ExternalInvoicingParty party, CancellationToken cancellationToken)
    {
        // Con el país pendiente (spec 2026-11-27) no se sabe a qué país factura: ningún lugar; el armador dice customer_country_missing.
        if (party.IsFinalConsumer || party.CountryPending)
        {
            return Place.None;
        }

        if (IsColombian(party.Country))
        {
            var codes = party.CityId is { } cityId ? await geography.FindCodesAsync(cityId, cancellationToken) : null;
            return codes is null ? Place.None : new Place(codes.CityCode, codes.StateCode, null, null);
        }

        if (string.IsNullOrWhiteSpace(party.CityName))
        {
            return Place.None;
        }

        var country = party.Country!.Trim().ToUpperInvariant();
        var cityText = party.CityName.Trim();
        var lookup = await geography.FindForeignCodesAsync(country, cityText, cancellationToken);
        return lookup.Codes is { } found
            ? new Place(found.CityCode, found.StateCode, found.CountryCode, null)
            : new Place(null, null, null, new SiigoUnlinkedCity(country, cityText, lookup.CountryInCatalog));
    }

    private sealed record Place(string? CityCode, string? StateCode, string? SiigoCountryCode, SiigoUnlinkedCity? UnlinkedCity)
    {
        public static Place None { get; } = new(null, null, null, null);
    }

    // Lo que no se puede partir viaja crudo: el armador del cliente decide si Siigo lo aceptaría. Con el país pendiente la región es CO
    // (IsColombian de null): la factura no sale hasta que se elija el país, así que el teléfono se parte como una ficha vieja.
    private SiigoPhone? PhoneOf(ExternalInvoicingParty party)
    {
        if (string.IsNullOrWhiteSpace(party.Phone))
        {
            return null;
        }

        var region = IsColombian(party.Country) ? "CO" : party.Country!.Trim().ToUpperInvariant();
        return phones.Split(party.Phone, region) is { } parts
            ? new SiigoPhone(parts.CallingCode, parts.NationalNumber)
            : new SiigoPhone(null, party.Phone);
    }

    // Un país vacío es el de la ficha vieja: Colombia.
    private static bool IsColombian(string? country) =>
        string.IsNullOrWhiteSpace(country) || string.Equals(country.Trim(), "CO", StringComparison.OrdinalIgnoreCase);

    // Por nombre, no por valor numérico: los dos enums se declaran aparte y la prueba de paridad exige los mismos nombres.
    private static T? Translate<T>(Enum? source)
        where T : struct, Enum =>
        source is not null && Enum.TryParse<T>(source.ToString(), out var value) ? value : null;
}
