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
        // Sólo Colombia tiene códigos DANE: una ciudad de otro país queda sin código y el armador la reporta como faltante.
        var codes = party is { IsFinalConsumer: false, CityId: { } cityId } && IsColombian(party.Country)
            ? await geography.FindCodesAsync(cityId, cancellationToken)
            : null;
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
                codes?.CityCode,
                codes?.StateCode,
                party.Email,
                PhoneOf(party)),
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

    // Lo que no se puede partir viaja crudo: el armador del cliente decide si Siigo lo aceptaría.
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
