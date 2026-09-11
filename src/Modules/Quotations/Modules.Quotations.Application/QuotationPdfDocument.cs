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
    /// <summary>El cliente recoge en la tienda: el documento imprime "Recoger en tienda" donde
    /// iría la entrega. Con esto <see cref="Shipping"/> sale vacía y sin
    /// <c>SameAsCustomer</c>, para que nada la lea como "se entrega en la dirección del
    /// cliente".</summary>
    bool IsStorePickup,
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
    /// <summary>La base sin IVA de todo el documento. El precio de los productos viene con el
    /// impuesto adentro, así que esto es <b>menos</b> que la suma de las líneas, no más.</summary>
    decimal Subtotal,
    /// <summary>La suma de los descuentos de línea. Para un mayorista que revende a valor
    /// público es, exactamente, lo que se gana: por eso el documento la imprime.</summary>
    decimal DiscountAmount,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal Total,
    /// <summary>Retención en la fuente que el cliente le retiene al vendedor (2,5% de la base
    /// sin IVA). Cero cuando el cliente no la practica. No resta del <see cref="Total"/>, que
    /// sigue siendo lo facturado — resta del <see cref="NetTotal"/>.</summary>
    decimal RetentionAmount,
    /// <summary>Lo que efectivamente se paga en efectivo: <see cref="Total"/> menos la
    /// retención. Igual al total cuando no hay retención.</summary>
    decimal NetTotal,
    /// <summary>El cliente tiene excedente de IVA y por eso el impuesto da cero. Viaja para que
    /// el documento pueda decirlo: un IVA en cero sin explicación se lee como error de
    /// cálculo.</summary>
    bool CustomerVatSurplus);

/// <summary>Una parte del documento: a quién se le factura, o a quién se le entrega.</summary>
/// <param name="SameAsCustomer">
/// Sin datos propios: se imprime una línea que lo dice, en vez de repetir por tercera vez lo que
/// el bloque "Cliente" ya muestra.
/// </param>
/// <param name="TaxId">
/// El NIT de la parte, cuando tiene uno fijo que imprimir: hoy sólo consumidor final
/// (<c>FinalConsumer.IdentificationNumber</c>). Vacío en el resto — una parte propia no guarda
/// identificación. Va aparte y no pegado a <see cref="Contact"/>: la plantilla parte el contacto
/// por " · " y el NIT necesita su rótulo. Default vacío para que las partes que no lo tienen no
/// tengan que decirlo.
/// </param>
public sealed record QuotationPdfParty(
    bool SameAsCustomer,
    string Name,
    string Contact,
    string Location,
    string TaxId = "");

public sealed record QuotationPdfBillingAccount(
    string CompanyName,
    string? CompanyTaxId,
    string BankName,
    string AccountNumber,
    string Currency);

/// <summary>
/// Una línea tal como el documento la imprime. Los tres importes están en la misma unidad —con
/// IVA adentro— a propósito: son las tres celdas de una fila que el cliente comprueba a mano, y
/// la comprobación es <c>DiscountedUnitPrice × Quantity = LineTotal</c>.
///
/// Acá no viaja <c>QuotationItem.Subtotal</c>, que es la base <b>sin</b> IVA de la línea. Ese
/// número es correcto y es lo que el encabezado suma, pero puesto al lado de un unitario que sí
/// trae impuesto da una fila que no multiplica.
/// </summary>
public sealed record QuotationPdfLine(
    string ProductName,
    decimal Quantity,
    /// <summary>Precio de lista con IVA incluido, snapshot al cotizar. La referencia comercial
    /// lo llama "valor público": es contra este precio que se mide el descuento.</summary>
    decimal UnitPrice,
    decimal DiscountPercentage,
    /// <summary>Lo que cuesta cada unidad ya con el descuento aplicado.</summary>
    decimal DiscountedUnitPrice,
    /// <summary>Lo que se cobra por la línea entera, IVA adentro.</summary>
    decimal LineTotal);

/// <summary>
/// Puerto hacia quien sabe dibujar un PDF. La implementación vive en Infrastructure porque
/// arrastra una librería de renderizado; Application sólo arma el documento y pide los bytes.
/// </summary>
public interface IQuotationPdfRenderer
{
    Task<byte[]> RenderAsync(
        QuotationPdfDocument document, CancellationToken cancellationToken);
}
