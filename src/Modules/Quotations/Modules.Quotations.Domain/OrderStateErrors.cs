namespace Modules.Quotations.Domain;

/// <summary>
/// Los códigos de las transiciones de los ejes del pedido (spec 2026-11-10 §3.4). Son contrato con el frontend, que
/// los mapea a texto: los que ya existían conservan su valor. <c>order.order.not_approved</c> se retiró con el estado
/// <c>Approved</c>; la guarda de edición usa <see cref="ExternalOrderErrors.NotEditable"/>.
/// </summary>
public static class OrderStateErrors
{
    public const string NotOpen = "order.order.not_open";

    public const string AlreadyPaid = "order.order.already_paid";

    public const string NotPaid = "order.order.not_paid";

    public const string AlreadyPrepared = "order.order.already_prepared";

    public const string NotPrepared = "order.order.not_prepared";

    public const string AlreadyArchived = "order.order.already_archived";

    public const string NotArchived = "order.order.not_archived";

    public const string AlreadyInvoiced = "order.order.already_invoiced";

    public const string NotInvoiced = "order.order.not_invoiced";

    public const string AlreadyCancelled = "order.order.already_cancelled";

    public const string PaymentProofRequired = "order.order.payment_proof_required";
}
