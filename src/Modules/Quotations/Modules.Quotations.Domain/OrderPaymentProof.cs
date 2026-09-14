namespace Modules.Quotations.Domain;

/// <summary>
/// Un comprobante de pago adjuntado durante la conversión de una cotización en pedido (US-14), o
/// sumado después mientras el pedido sigue pendiente (<see cref="Order.AddPaymentProofs"/>).
/// Entidad hija de <see cref="Order"/>: el archivo y quién lo subió no cambian una vez creado,
/// pero el monto sí puede corregirse (a pedido, 2026-09) —ver <see cref="UpdateAmount"/>— si
/// alguien lo tipeó mal.
/// </summary>
public sealed class OrderPaymentProof
{
    private OrderPaymentProof()
    {
    }

    private OrderPaymentProof(
        OrderPaymentProofId id,
        OrderId orderId,
        Guid fileId,
        decimal amount,
        MemberId uploadedBy,
        DateTimeOffset uploadedAt)
    {
        Id = id;
        OrderId = orderId;
        FileId = fileId;
        Amount = amount;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
    }

    public OrderPaymentProofId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    /// <summary>Referencia blanda al archivo en el módulo Storage — mismo mecanismo que
    /// <see cref="Quotation.PdfFileId"/>. Que el archivo exista, sea del tenant, ya haya
    /// terminado de subir y sea uno de los tipos aceptados (PDF/JPG/PNG, hasta 10 MB) lo valida
    /// la aplicación con <c>IQuotationFileLookup</c> antes de construir el pedido.</summary>
    public Guid FileId { get; private set; }

    /// <summary>Monto que cubre este comprobante específico — cada archivo puede tener el suyo
    /// (US-14).</summary>
    public decimal Amount { get; private set; }

    public MemberId UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    internal static OrderPaymentProof Create(
        OrderPaymentProofId id,
        OrderId orderId,
        Guid fileId,
        decimal amount,
        MemberId uploadedBy,
        DateTimeOffset uploadedAt)
    {
        if (fileId == Guid.Empty)
        {
            throw new QuotationsDomainException(
                "sale.payment_proof.file_required",
                "The payment proof file is required.");
        }

        if (amount <= 0)
        {
            throw new QuotationsDomainException(
                "sale.payment_proof.amount_invalid",
                "The payment proof amount must be greater than zero.");
        }

        return new OrderPaymentProof(id, orderId, fileId, amount, uploadedBy, uploadedAt);
    }

    /// <summary>Corrige el monto de un comprobante ya cargado (a pedido, 2026-09) — sólo desde
    /// <see cref="Order.AddPaymentProofs"/>, que es quien decide si el pedido admite el cambio
    /// (sólo <see cref="OrderStatus.Pending"/>). El archivo y quién lo subió no se tocan: es una
    /// corrección puntual del importe, no otro comprobante.</summary>
    internal void UpdateAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new QuotationsDomainException(
                "sale.payment_proof.amount_invalid",
                "The payment proof amount must be greater than zero.");
        }

        Amount = amount;
    }
}
