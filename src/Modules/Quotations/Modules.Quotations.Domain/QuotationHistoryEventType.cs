namespace Modules.Quotations.Domain;

/// <summary>
/// El vocabulario completo de eventos de modelo-datos-cotizaciones.md §2.3: creación/edición de
/// un borrador, envío (US-12), anulación (US-11), vencimiento automático (US-19) y aprobación al
/// convertir en venta (US-16).
/// </summary>
public enum QuotationHistoryEventType
{
    Created,
    Edited,
    Sent,
    Voided,
    Expired,
    Approved
}
