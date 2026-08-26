namespace Modules.Quotations.Domain;

/// <summary>
/// Máquina de estados de la cotización (modelo-datos-cotizaciones.md §3). Esta fase sólo crea y
/// edita cotizaciones en <see cref="Draft"/>; el resto de las transiciones (envío, conversión a
/// venta, anulación, vencimiento) llegan en fases posteriores, pero los cinco valores se declaran
/// juntos porque ya están confirmados como el vocabulario completo del negocio.
/// </summary>
public enum QuotationStatus
{
    Draft,
    Sent,
    Approved,
    Expired,
    Voided
}
