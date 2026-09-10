using Modules.Quotations.Domain;

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

    public static QuotationPdfDocument From(QuotationResponse quotation) =>
        new(
            quotation.QuotationNumber,
            quotation.CreatedAt,
            quotation.ValidUntil,
            quotation.Client?.Name ?? string.Empty,
            quotation.Client?.Cuc ?? string.Empty,
            Join(ContactSeparator, quotation.Client?.Phone, quotation.Client?.Email),
            Join(LocationSeparator, quotation.Client?.Address, quotation.Client?.CityName),
            PartyFor(quotation, QuotationPartyRole.Billing),
            PartyFor(quotation, QuotationPartyRole.Shipping),
            quotation.AdvisorEmail ?? string.Empty,
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
            quotation.CustomerVatSurplus);

    /// <summary>
    /// Los importes que el cliente comprueba con la calculadora.
    ///
    /// <c>QuotationItemResponse.Subtotal</c> es la base <b>sin</b> IVA de la línea, mientras que
    /// <c>UnitPrice</c> viene <b>con</b> el impuesto adentro (<c>QuotationItem.Apply</c>).
    /// Imprimir esos dos juntos daba una fila que no multiplica: 12 × 35.900 no da 307.714. Acá
    /// se rearma el total bruto con la misma fórmula y los mismos redondeos que el dominio.
    /// </summary>
    private static QuotationPdfLine LineFor(QuotationItemResponse item)
    {
        var lineTotal = Round(item.Quantity * item.UnitPrice) - item.DiscountAmount;

        // El unitario con descuento se deriva del total, y no al revés. Calculado como
        // `UnitPrice × (1 - %)` el redondeo puede dejar Cantidad × Unitario distinto del total
        // impreso al lado, que es justo la comprobación que el documento tiene que sobrevivir.
        // La cantidad es siempre mayor que cero por invariante del dominio; el guardia es contra
        // un DTO armado a mano, no contra el agregado.
        var discountedUnitPrice = item.Quantity > 0 ? Round(lineTotal / item.Quantity) : 0m;

        return new QuotationPdfLine(
            item.ProductName,
            item.Quantity,
            item.UnitPrice,
            item.DiscountPercentage,
            discountedUnitPrice,
            lineTotal);
    }

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

    private static QuotationPdfBillingAccount? BillingAccountFor(QuotationResponse quotation) =>
        quotation.BillingAccount is { } account
            ? new QuotationPdfBillingAccount(
                // Null cuando la empresa se borró: el id es una referencia blanda entre módulos
                // y la cotización tiene que poder exportarse igual.
                account.CompanyName ?? string.Empty,
                account.CompanyTaxId,
                account.BankName,
                account.AccountNumber,
                account.Currency)
            : null;

    private static string Join(string separator, params string?[] parts) =>
        string.Join(
            separator,
            parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
