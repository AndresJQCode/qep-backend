namespace Modules.Quotations.Application;

/// <summary>
/// Todo lo que el PDF de una cotización imprime, ya resuelto: nombres de cliente, de producto y
/// de ciudad, y las partes de facturación y entrega tal como se leen. Quien lo dibuja no vuelve
/// a consultar nada — si el cliente o el producto cambiaron después de cotizar, el documento
/// sigue diciendo lo que se cotizó.
///
/// Es el modelo que antes vivía en el navegador (<c>QuotePdfData</c>, <c>quote-pdf.ts</c>).
/// Se mudó entero acá: el documento que se le manda al cliente no puede depender de qué pantalla
/// lo pidió ni de qué versión del frontend estaba abierta.
/// </summary>
public sealed record QuotationPdfDocument(
    string QuotationNumber,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
    string CustomerName,
    string CustomerCuc,
    /// <summary>Teléfono y correo en una línea, ya unidos.</summary>
    string CustomerContact,
    /// <summary>Dirección y ciudad en una línea, ya unidas.</summary>
    string CustomerLocation,
    QuotationPdfParty Billing,
    QuotationPdfParty Shipping,
    string AdvisorLabel,
    /// <summary>La moneda de todos los importes del documento, la de la cuenta de cobro: un PDF
    /// que cobra a una cuenta en dólares imprime dólares.</summary>
    string Currency,
    /// <summary>A nombre de quién y a qué cuenta se paga. Nulo si la cotización todavía no
    /// eligió cuenta: el documento sale igual, sin ese pie.</summary>
    QuotationPdfBillingAccount? BillingAccount,
    string? PaymentMethod,
    string? Notes,
    IReadOnlyList<QuotationPdfLine> Items,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal Total);

/// <summary>Una parte del documento: a quién se le factura, o a quién se le entrega.</summary>
/// <param name="SameAsCustomer">
/// Sin datos propios: se imprime una línea que lo dice, en vez de repetir por tercera vez lo que
/// el bloque "Cliente" ya muestra.
/// </param>
public sealed record QuotationPdfParty(
    bool SameAsCustomer,
    string Name,
    string Contact,
    string Location);

public sealed record QuotationPdfBillingAccount(
    string CompanyName,
    string? CompanyTaxId,
    string BankName,
    string AccountNumber,
    string Currency);

public sealed record QuotationPdfLine(
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal Subtotal);

/// <summary>
/// Puerto hacia quien sabe dibujar un PDF. La implementación vive en Infrastructure porque
/// arrastra una librería de renderizado; Application sólo arma el documento y pide los bytes.
/// </summary>
public interface IQuotationPdfRenderer
{
    byte[] Render(QuotationPdfDocument document);
}
