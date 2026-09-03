namespace Modules.Quotations.Domain;

/// <summary>
/// Máquina de estados de la cotización (modelo-datos-cotizaciones.md §3). Cuatro valores
/// confirmados como el vocabulario completo del negocio: borrador, enviada, anulada, vencida.
///
/// No hay un estado "aprobada"/"convertida": convertir una cotización en venta
/// (<c>ConvertQuotationToSaleHandler</c>) la deja en <see cref="Sent"/> — la <c>Sale</c> creada,
/// que referencia <c>QuotationId</c> 1:1, es la única señal de que ya se convirtió. Antes existía
/// <see cref="Approved"/>(referenciado en filas y en <c>QuotationHistoryEventType</c>, que sigue
/// existiendo como hecho histórico); la migración <c>NormalizeApprovedQuotationStatus</c> pasa
/// cualquier fila vieja con ese valor a <see cref="Sent"/> antes de que el modelo deje de
/// reconocerlo — <c>Status</c> se guarda como texto (<c>HasConversion&lt;string&gt;()</c>), así
/// que una fila con un valor que el enum ya no tiene rompería al leerla.
/// </summary>
public enum QuotationStatus
{
    Draft,
    Sent,
    Voided,
    Expired
}
