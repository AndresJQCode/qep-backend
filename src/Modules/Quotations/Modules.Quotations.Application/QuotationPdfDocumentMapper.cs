using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el documento que se le manda a `qcode-pdf` a partir de la **misma** respuesta que
/// dibuja la pantalla. No vuelve a consultar nada: `QuotationResponseComposer` ya resolvió el
/// cliente, la empresa de la cuenta de cobro y el nombre de cada producto.
///
/// Que el PDF salga de ahí y no del agregado es deliberado: si la pantalla y el documento se
/// armaran por caminos distintos, terminarían diciendo cosas distintas de la misma cotización, y
/// la diferencia la descubriría el cliente.
///
/// Lo único que decide este mapeo es la forma de las líneas que el `.typ` imprime como texto
/// corrido. Esa lógica va acá y no en la plantilla porque Typst no tiene que saber qué hacer
/// cuando un cliente no tiene correo.
/// </summary>
public static class QuotationPdfDocumentMapper
{
    private const string ContactSeparator = " · ";
    private const string LocationSeparator = ", ";

    /// <summary>El <paramref name="calendar"/> es el del tenant de la cotización: la fecha de emisión
    /// se imprime en su día, no en el de UTC (spec 2026-09-17, punto 7).</summary>
    public static QuotationPdfDocument From(
        QuotationResponse quotation, TenantCalendar calendar, QuotationPdfLogo? logo) =>
        new(
            quotation.QuotationNumber,
            DateOnly.FromDateTime(calendar.ToLocal(quotation.CreatedAt).DateTime),
            quotation.ValidUntil,
            quotation.Client?.Name ?? string.Empty,
            quotation.Client?.Cuc ?? string.Empty,
            Join(ContactSeparator, quotation.Client?.Phone, quotation.Client?.Email),
            Join(LocationSeparator, quotation.Client?.Address, quotation.Client?.CityName),
            BillingFor(quotation),
            ShippingFor(quotation),
            quotation.IsStorePickup,
            AdvisorLabelFor(quotation),
            quotation.Currency,
            BillingAccountFor(quotation),
            quotation.PaymentMethod,
            quotation.Notes,
            [.. quotation.Items.Select(LineFor)],
            quotation.Subtotal,
            quotation.DiscountAmount,
            quotation.TaxPercentage,
            quotation.TaxAmount,
            quotation.Total,
            quotation.RetentionAmount,
            quotation.NetTotal,
            quotation.CustomerVatSurplus,
            logo);

    /// <summary>
    /// Los importes que el cliente comprueba con la calculadora.
    ///
    /// <c>QuotationItemResponse.Subtotal</c> es la base <b>sin</b> IVA de la línea, mientras que
    /// <c>UnitPrice</c> viene <b>con</b> el impuesto adentro (<c>QuotationItem.Apply</c>).
    /// Imprimir esos dos juntos daba una fila que no multiplica: 12 × 35.900 no da 307.714. Acá
    /// se rearma el total bruto con la misma fórmula y los mismos redondeos que el dominio.
    ///
    /// El unitario con descuento ya no se deriva acá: lo calcula la línea y viaja en el DTO
    /// (<c>QuotationItem.DiscountedUnitPrice</c>), desde que la pantalla lo muestra en una
    /// columna propia. Tenerlo en un solo lado es lo que evita que el PDF y la pantalla impriman
    /// números distintos.
    /// </summary>
    private static QuotationPdfLine LineFor(QuotationItemResponse item) =>
        new(item.ProductName,
            item.Quantity,
            item.UnitPrice,
            item.DiscountPercentage,
            item.DiscountedUnitPrice,
            Round(item.Quantity * item.UnitPrice) - item.DiscountAmount);

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Sin datos propios la parte se imprime como "los del cliente", en vez de repetir
    /// por tercera vez lo que el bloque Cliente ya muestra.</summary>
    private static QuotationPdfParty PartyFor(QuotationResponse quotation, QuotationPartyRole role)
    {
        var party = quotation.Parties.FirstOrDefault(
            value => string.Equals(value.Role, role.ToString(), StringComparison.OrdinalIgnoreCase));

        return party is null
            ? new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty)
            : new QuotationPdfParty(
                false,
                party.Name ?? string.Empty,
                Join(ContactSeparator, party.Phone, party.Email),
                party.Address ?? string.Empty);
    }

    /// <summary>Consumidor final no tiene fila de facturación —el dominio la prohíbe— pero
    /// tampoco es "los del cliente": la factura sale a otro nombre, con el NIT genérico. Sin
    /// contacto ni dirección, que serían los del cliente real. Nombre y NIT salen de
    /// <see cref="FinalConsumer"/>, el único lugar del backend donde viven.</summary>
    private static QuotationPdfParty BillingFor(QuotationResponse quotation) =>
        quotation.BillsToFinalConsumer
            ? new QuotationPdfParty(
                false,
                FinalConsumer.Name,
                string.Empty,
                string.Empty,
                FinalConsumer.IdentificationNumber)
            : PartyFor(quotation, QuotationPartyRole.Billing);

    /// <summary>Recoger en tienda no tiene a dónde entregar: ni fila propia —el dominio la
    /// borra— ni "los del cliente", que se leería como "se entrega en la dirección del
    /// cliente". La parte sale vacía y quien dice qué pasa es <c>IsStorePickup</c>.</summary>
    private static QuotationPdfParty ShippingFor(QuotationResponse quotation) =>
        quotation.IsStorePickup
            ? new QuotationPdfParty(false, string.Empty, string.Empty, string.Empty)
            : PartyFor(quotation, QuotationPartyRole.Shipping);

    /// <summary>La ficha "Asesor" presenta a la persona por su nombre, y cae al correo mientras la
    /// membresía no tenga uno (filas anteriores al nombre y el owner). Sin ninguno de los dos,
    /// vacío: <c>quotation.typ</c> ya lo resuelve. Un PDF ya generado conserva lo que imprimió:
    /// el caché sólo se invalida por <c>Quotation.Version</c> (spec 2026-09-11, D7).</summary>
    private static string AdvisorLabelFor(QuotationResponse quotation) =>
        quotation.AdvisorName ?? quotation.AdvisorEmail ?? string.Empty;

    private static QuotationPdfBillingAccount? BillingAccountFor(QuotationResponse quotation) =>
        quotation.BillingAccount is { } account
            ? new QuotationPdfBillingAccount(
                // Null cuando la empresa se borró: el id es una referencia blanda entre módulos
                // y la cotización tiene que poder exportarse igual.
                account.CompanyName ?? string.Empty,
                account.CompanyTaxId,
                account.CompanyAddress ?? string.Empty,
                account.CompanyPhone ?? string.Empty,
                account.BankName,
                account.AccountNumber,
                account.Currency)
            : null;

    private static string Join(string separator, params string?[] parts) =>
        string.Join(
            separator,
            parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
