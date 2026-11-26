namespace Modules.Quotations.Domain;

/// <summary>
/// El pago del pedido (spec 2026-11-10 §3.2). Lo calcula el sistema con los comprobantes: nada consignado es
/// <see cref="PaymentPending"/>, menos que el neto es <see cref="PartialPaymentReceived"/> y lo que lo cubre es
/// <see cref="PendingVerification"/> («Por verificar»). <see cref="FullPaymentReceived"/> («Pagado») sólo se alcanza
/// marcándolo a mano o por la plataforma. La columna guarda el nombre: el nuevo va al final.
/// </summary>
public enum OrderPaymentStatus
{
    FullPaymentReceived,
    PartialPaymentReceived,
    PaymentPending,
    PendingVerification
}
