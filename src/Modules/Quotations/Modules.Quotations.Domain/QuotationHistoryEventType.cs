namespace Modules.Quotations.Domain;

/// <summary>
/// El vocabulario completo de eventos de modelo-datos-cotizaciones.md §2.3: creación/edición de
/// un borrador, envío (US-12), anulación (US-11), vencimiento automático (US-19) y aprobación al
/// convertir en pedido (US-16).
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

    /// <summary>
    /// Un intento de envío que se cayó. No es un estado del agregado --la cotización sigue
    /// en borrador-- sino un hecho de la línea de tiempo: sin esto, quien mira una cotización
    /// que "no se envía" no ve rastro de los tres intentos anteriores. El detalle técnico no
    /// vive acá, vive en <c>quotation_send_failures</c>.
    /// </summary>
    SendFailed,
    Voided,
    Expired,
    Approved,

    /// <summary>
    /// Cambió el porcentaje de una retención municipal (spec ReteICA por empresa §7.8): el asesor lo editó o lo
    /// restableció, o una re-resolución descartó uno editado. Va al final: se guarda como texto.
    /// </summary>
    WithholdingRateEdited,

    /// <summary>
    /// El asesor la marcó como enviada por fuera de QEP (spec 2026-11-17): pasó a <c>Sent</c> sin que QEP mandara nada.
    /// Va aparte de <see cref="Sent"/> porque ese dice que QEP sí la entregó con su PDF. Va al final: se guarda como texto.
    /// </summary>
    MarkedSent,
}
