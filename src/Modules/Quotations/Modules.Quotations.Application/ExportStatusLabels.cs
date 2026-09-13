using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Cómo se nombra cada estado en el Excel de las exportaciones (spec 2026-09-13, A7). Son las etiquetas
/// de las tablas de los listados (qep-frontend: quote-status-badge.tsx y sale-list.ts), porque el
/// archivo es "lo que estoy viendo, entero" y tiene que decir lo mismo que la pantalla de la que sale.
///
/// Sólo para el archivo, que es lo que lee una persona. La API sigue mandando el nombre del enum (A8):
/// es contrato, y el diccionario de la pantalla lo tiene el frontend.
///
/// La rama por defecto tira a propósito: un estado nuevo sin etiqueta rompe acá, y
/// ExportStatusLabelsTests recorre los tres enums para que eso se vea en CI y no en producción.
/// </summary>
public static class ExportStatusLabels
{
    public static string For(QuotationStatus status) => status switch
    {
        QuotationStatus.Draft => "Borrador",
        QuotationStatus.Sent => "Enviada",
        QuotationStatus.Voided => "Anulada",
        QuotationStatus.Expired => "Vencida",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The quotation status has no export label."),
    };

    public static string For(SaleStatus status) => status switch
    {
        SaleStatus.Pending => "Pendiente",
        SaleStatus.Approved => "Aprobada",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The sale status has no export label."),
    };

    public static string For(SalePaymentStatus status) => status switch
    {
        SalePaymentStatus.FullPaymentReceived => "Pago total",
        SalePaymentStatus.PartialPaymentReceived => "Pago parcial",
        SalePaymentStatus.PaymentPending => "Pago pendiente",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The payment status has no export label."),
    };
}
