namespace Modules.Quotations.Domain;

/// <summary>
/// Máquina de estados de la cotización (modelo-datos-cotizaciones.md §3): borrador, enviada,
/// anulada, vencida y convertida.
///
/// <see cref="Converted"/> lo pone <c>ConvertQuotationToSaleHandler</c> (vía
/// <c>Quotation.ConvertToSale</c>), en la misma unidad de trabajo que crea la <c>Sale</c>. Desde
/// ahí la cotización es de sólo lectura: no se edita, no se anula, no se reenvía, no vence y no
/// se convierte otra vez. La <c>Sale</c> sigue apuntándola 1:1 por <c>QuotationId</c>: el estado
/// dice que ya se convirtió, la venta dice a cuál ir y si ya se aprobó.
///
/// Hasta el 2026-09-13 convertir la dejaba en <see cref="Sent"/> y la venta era la única señal.
/// Las que se convirtieron antes siguen en <see cref="Sent"/>: no hubo backfill.
///
/// Historia: existió un <c>Approved</c> que la migración <c>NormalizeApprovedQuotationStatus</c>
/// pasó a <see cref="Sent"/> antes de sacarlo del enum. <c>Status</c> se guarda como texto
/// (<c>HasConversion&lt;string&gt;()</c>), así que agregar un valor no necesita migración, pero
/// quitarlo sí: una fila con un valor que el enum ya no tiene rompe al leerla.
/// </summary>
public enum QuotationStatus
{
    Draft,
    Sent,
    Voided,
    Expired,
    Converted
}
