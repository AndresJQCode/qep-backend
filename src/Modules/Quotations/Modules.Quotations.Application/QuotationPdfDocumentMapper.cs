using System.Globalization;
using BuildingBlocks.Domain.Catalog;
using BuildingBlocks.Domain.Localization;
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
    /// se imprime en su día, no en el de UTC (spec 2026-09-17, punto 7). El <paramref name="numberFormat"/>
    /// es el del tenant y sólo decide los separadores (spec 2026-10-23, D-R1).</summary>
    public static QuotationPdfDocument From(
        QuotationResponse quotation,
        TenantCalendar calendar,
        QuotationPdfLogo? logo,
        string? numberFormat = null,
        string? language = null)
    {
        var currency = Currencies.Get(quotation.Currency);
        var (thousands, decimals) = RegionalNumberFormat.Separators(numberFormat);
        return new(
            quotation.QuotationNumber,
            DateOnly.FromDateTime(calendar.ToLocal(quotation.CreatedAt).DateTime),
            quotation.ValidUntil,
            quotation.Client?.Name ?? string.Empty,
            quotation.Client?.Cuc ?? string.Empty,
            Join(ContactSeparator, quotation.Client?.Phone, quotation.Client?.Email),
            Join(LocationSeparator, quotation.Client?.Address, quotation.Client?.CityName),
            BillingFor(quotation.Parties, quotation.BillsToFinalConsumer),
            ShippingFor(quotation.Parties, quotation.IsStorePickup),
            quotation.IsStorePickup,
            AdvisorLabelFor(quotation.AdvisorName, quotation.AdvisorEmail),
            quotation.Currency,
            currency.Symbol,
            currency.Decimals,
            BillingAccountFor(quotation.BillingAccount),
            quotation.PaymentMethod,
            LinesFor(quotation.Items),
            quotation.Subtotal,
            quotation.DiscountAmount,
            quotation.TaxPercentage,
            quotation.TaxAmount,
            quotation.Total,
            quotation.RetentionAmount,
            WithholdingsFor(quotation.Withholdings, decimals),
            quotation.NetTotal,
            quotation.CustomerVatSurplus,
            logo,
            quotation.Shipping.Carrier?.Name,
            quotation.Shipping.ChargeMode,
            quotation.Shipping.FreightAmount,
            quotation.Shipping.FreightTaxPercentage,
            quotation.FreightSubtotal,
            quotation.FreightTaxAmount,
            quotation.Shipping.CashOnDelivery,
            ThousandsSeparator: thousands,
            DecimalSeparator: decimals,
            Language: SupportedLanguages.OrDefault(language));
    }

    /// <summary>
    /// El mismo documento para un pedido (owner: «layout único para cotización y pedido»), desde la
    /// misma respuesta que dibuja su pantalla (<see cref="OrderDetailResponse"/>) y por los mismos
    /// motivos que <see cref="From"/>. Todo sale del pedido —partes, cuenta de cobro, líneas, totales,
    /// envío—, nunca de la cotización de origen, que puede no existir (pedido manual o de Shopify).
    ///
    /// La emisión es la fecha del pedido (<see cref="OrderResponse.OrderDate"/>, spec 2026-11-16). No hay vigencia: un pedido
    /// no vence, y la plantilla omite el renglón con <see cref="QuotationPdfKind.Order"/>.
    /// </summary>
    public static QuotationPdfDocument FromOrder(
        OrderDetailResponse order,
        TenantCalendar calendar,
        QuotationPdfLogo? logo,
        string? numberFormat = null,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(calendar);
        var header = order.Order;
        var currency = Currencies.Get(header.Currency);
        var (thousands, decimals) = RegionalNumberFormat.Separators(numberFormat);
        return new(
            header.OrderNumber,
            // Spec 2026-11-16 §5: la fecha del pedido, que el asesor puede corregir; ya es un día del tenant.
            header.OrderDate,
            ValidUntil: null,
            order.Client?.Name ?? string.Empty,
            order.Client?.Cuc ?? string.Empty,
            Join(ContactSeparator, order.Client?.Phone, order.Client?.Email),
            Join(LocationSeparator, order.Client?.Address, order.Client?.CityName),
            BillingFor(header.Parties, header.BillsToFinalConsumer),
            ShippingFor(header.Parties, header.IsStorePickup),
            header.IsStorePickup,
            AdvisorLabelFor(order.AdvisorName, order.AdvisorEmail),
            header.Currency,
            currency.Symbol,
            currency.Decimals,
            BillingAccountFor(order.BillingAccount),
            header.PaymentMethod,
            LinesFor(order.Items),
            header.Subtotal,
            header.DiscountAmount,
            header.TaxPercentage,
            header.TaxAmount,
            header.Total,
            header.RetentionAmount,
            WithholdingsFor(order.Order.Withholdings, decimals),
            header.NetTotal,
            header.CustomerVatSurplus,
            logo,
            // Sin transportadora reconocida, el nombre con el que la tienda llamó al envío (spec de
            // Shopify §6.2.5): es lo único que dice cómo viaja el pedido. La pantalla lo muestra igual.
            header.Shipping.Carrier?.Name ?? header.Shipping.ExternalShippingTitle,
            header.Shipping.ChargeMode,
            header.Shipping.FreightAmount,
            header.Shipping.FreightTaxPercentage,
            header.FreightSubtotal,
            header.FreightTaxAmount,
            header.Shipping.CashOnDelivery,
            QuotationPdfKind.Order,
            thousands,
            decimals,
            Language: SupportedLanguages.OrDefault(language));
    }

    /// <summary>Las respuestas ya traen sólo las que aplicaron (<c>AppliedWithholdings</c>), en su orden: una fila
    /// del documento por cada una, rotulada con el nombre de la tasa y su porcentaje efectivo (spec ReteICA por empresa
    /// §7.9). Sin ninguna, sin filas.</summary>
    private static QuotationPdfWithholding[] WithholdingsFor(
        IReadOnlyList<WithholdingResponse> withholdings, string decimalSeparator) =>
        [
            .. withholdings.Select(withholding => new QuotationPdfWithholding(
                ScopedName(withholding), withholding.Amount, PercentageLabel(withholding.Percentage, decimalSeparator))),
        ];

    // Spec 2026-11-18 §4.4: dos tasas pueden compartir nombre y porcentaje si son de distinto alcance; el rótulo dice a qué
    // líneas aplica. Una sin alcance (All) sale con su nombre, como antes.
    private static string ScopedName(WithholdingResponse withholding) => withholding.AppliesTo switch
    {
        "Goods" => $"{withholding.Name} (bienes)",
        "Services" => $"{withholding.Name} (servicios)",
        _ => withholding.Name,
    };

    // Plan P10: el mismo patrón que HistoryFormat.Percentage, con el separador decimal del tenant. Un porcentaje no lleva
    // miles, así que basta el decimal.
    private static string PercentageLabel(decimal percentage, string decimalSeparator) =>
        percentage.ToString("0.###", new NumberFormatInfo { NumberDecimalSeparator = decimalSeparator }) + " %";

    /// <summary>
    /// Los importes que el cliente comprueba con la calculadora.
    ///
    /// <c>QuotationItemResponse.Subtotal</c> es la base <b>sin</b> IVA de la línea, mientras que
    /// <c>UnitPrice</c> viene <b>con</b> el impuesto adentro (<c>QuotationItem.Apply</c>).
    /// Imprimir esos dos juntos daba una fila que no multiplica: 12 × 35.900 no da 307.714. Acá
    /// el total de línea es <c>Subtotal + TaxAmount</c>, igual que <c>LineTotal</c> en el dominio: desde el
    /// 2026-11-23 el descuento de línea es sin IVA y ya no se puede restar del bruto con IVA.
    ///
    /// El unitario con descuento ya no se deriva acá: lo calcula la línea y viaja en el DTO
    /// (<c>QuotationItem.DiscountedUnitPriceUnder</c>, con la regla de redondeo del documento), desde que la pantalla lo muestra en una
    /// columna propia. Tenerlo en un solo lado es lo que evita que el PDF y la pantalla impriman
    /// números distintos.
    /// </summary>
    private static QuotationPdfLine LineFor(QuotationItemResponse item, bool withUnit) =>
        new(item.ProductName,
            item.Quantity,
            item.UnitPrice,
            item.DiscountPercentage,
            item.DiscountedUnitPrice,
            item.Subtotal + item.TaxAmount,
            withUnit ? SalesUnitLabels.Spanish(Enum.Parse<SalesUnit>(item.SalesUnit), item.Quantity) : null,
            item.ProductCode);

    /// <summary>
    /// Spec 2026-11-18 §6: con todas las líneas en <c>Unit</c> ninguna lleva unidad, para que el PDF de un tenant que no
    /// usa unidades salga idéntico al de antes. Con una sola distinta, todas la llevan, también las de «unidad»: una
    /// columna con la unidad en unas filas y en otras no se lee como un olvido.
    /// </summary>
    private static QuotationPdfLine[] LinesFor(IReadOnlyCollection<QuotationItemResponse> items)
    {
        var withUnit = items.Any(item => item.SalesUnit != nameof(SalesUnit.Unit));
        return [.. items.Select(item => LineFor(item, withUnit))];
    }

    /// <summary>Sin datos propios la parte se imprime como "los del cliente", en vez de repetir
    /// por tercera vez lo que el bloque Cliente ya muestra.</summary>
    private static QuotationPdfParty PartyFor(
        IReadOnlyCollection<QuotationPartyResponse> parties, QuotationPartyRole role)
    {
        var party = parties.FirstOrDefault(
            value => string.Equals(value.Role, role.ToString(), StringComparison.OrdinalIgnoreCase));

        return party is null
            ? new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty)
            : new QuotationPdfParty(
                false,
                party.Name ?? string.Empty,
                Join(ContactSeparator, party.Phone, party.Email),
                // El código postal va tras la dirección (spec 2026-11-24; sólo la entrega lo trae). Fuera de Colombia la
                // parte guarda la ciudad como texto: se imprime después, con el país.
                IsForeign(party)
                    ? Join(LocationSeparator, party.Address, party.PostalCode, Join(LocationSeparator, party.CityName, party.Country))
                    : Join(LocationSeparator, party.Address, party.PostalCode),
                // Sólo la facturación guarda documento (la entrega lo descarta en el dominio); una
                // fila anterior a la columna no tiene y queda vacío, que la plantilla no imprime.
                role == QuotationPartyRole.Billing ? party.IdentificationNumber ?? string.Empty : string.Empty);
    }

    private static bool IsForeign(QuotationPartyResponse party) =>
        !string.IsNullOrWhiteSpace(party.Country)
        && !string.Equals(party.Country, QuotationPartyDetails.ColombiaCountryCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Consumidor final no tiene fila de facturación —el dominio la prohíbe— pero
    /// tampoco es "los del cliente": la factura sale a otro nombre, con el NIT genérico. Sin
    /// contacto ni dirección, que serían los del cliente real. Nombre y NIT salen de
    /// <see cref="FinalConsumer"/>, el único lugar del backend donde viven.</summary>
    private static QuotationPdfParty BillingFor(
        IReadOnlyCollection<QuotationPartyResponse> parties, bool billsToFinalConsumer) =>
        billsToFinalConsumer
            ? new QuotationPdfParty(
                false,
                FinalConsumer.Name,
                string.Empty,
                string.Empty,
                FinalConsumer.IdentificationNumber)
            : PartyFor(parties, QuotationPartyRole.Billing);

    /// <summary>Recoger en tienda no tiene a dónde entregar: ni fila propia —el dominio la
    /// borra— ni "los del cliente", que se leería como "se entrega en la dirección del
    /// cliente". La parte sale vacía y quien dice qué pasa es <c>IsStorePickup</c>.</summary>
    private static QuotationPdfParty ShippingFor(
        IReadOnlyCollection<QuotationPartyResponse> parties, bool isStorePickup) =>
        isStorePickup
            ? new QuotationPdfParty(false, string.Empty, string.Empty, string.Empty)
            : PartyFor(parties, QuotationPartyRole.Shipping);

    /// <summary>La ficha "Asesor" presenta a la persona por su nombre, y cae al correo mientras la
    /// membresía no tenga uno (filas anteriores al nombre y el owner). Sin ninguno de los dos,
    /// vacío: <c>quotation.typ</c> ya lo resuelve. Un PDF ya generado conserva lo que imprimió:
    /// el caché sólo se invalida por <c>Quotation.Version</c> (spec 2026-09-11, D7).</summary>
    private static string AdvisorLabelFor(string? advisorName, string? advisorEmail) =>
        advisorName ?? advisorEmail ?? string.Empty;

    private static QuotationPdfBillingAccount? BillingAccountFor(QuotationBillingResponse? billingAccount) =>
        billingAccount is { } account
            ? new QuotationPdfBillingAccount(
                // Null cuando la empresa se borró: el id es una referencia blanda entre módulos
                // y la cotización tiene que poder exportarse igual.
                account.CompanyName ?? string.Empty,
                account.CompanyTaxId,
                CompanyDocumentLabels.For(account.CompanyDocumentType),
                account.CompanyAddress ?? string.Empty,
                account.CompanyPhone ?? string.Empty,
                // Null en una empresa sin cuenta (spec 2026-11-17 §4.2): la plantilla no pinta el bloque de pago.
                account.BankName,
                account.AccountNumber,
                account.Currency)
            : null;

    private static string Join(string separator, params string?[] parts) =>
        string.Join(
            separator,
            parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
