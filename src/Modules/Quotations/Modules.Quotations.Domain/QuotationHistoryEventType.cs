namespace Modules.Quotations.Domain;

/// <summary>
/// El vocabulario completo de eventos de modelo-datos-cotizaciones.md §2.3: creación/edición de
/// un borrador, envío (US-12), anulación (US-11), vencimiento automático (US-19) y aprobación al
/// convertir en venta (US-16).
///
/// <see cref="Resent"/> se agregó después: un envío posterior al primero es el mismo hecho para
/// el agregado (<c>Send</c> no distingue) pero no para quien lee el historial — "la cotización
/// se le mandó tres veces al cliente" sólo se puede reconstruir si cada reenvío se anota como
/// tal. Se guarda como texto (<c>HasConversion&lt;string&gt;()</c>, <c>MaxLength(50)</c>), así
/// que sumar un valor no necesita migración.
/// </summary>
public enum QuotationHistoryEventType
{
    Created,
    Edited,
    Sent,
    Resent,
    Voided,
    Expired,
    Approved
}
