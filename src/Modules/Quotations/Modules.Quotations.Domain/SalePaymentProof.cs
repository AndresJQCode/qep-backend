namespace Modules.Quotations.Domain;

/// <summary>
/// Un comprobante de pago adjuntado durante la conversión de una cotización en venta (US-14).
/// Entidad hija de <see cref="Sale"/> — nace con ella y no se agrega ni se quita después: el
/// asistente de conversión los sube todos en el mismo paso.
/// </summary>
public sealed class SalePaymentProof
{
    private SalePaymentProof()
    {
    }

    private SalePaymentProof(
        SalePaymentProofId id,
        SaleId saleId,
        Guid fileId,
        decimal amount,
        MemberId uploadedBy,
        DateTimeOffset uploadedAt)
    {
        Id = id;
        SaleId = saleId;
        FileId = fileId;
        Amount = amount;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
    }

    public SalePaymentProofId Id { get; private set; }

    public SaleId SaleId { get; private set; }

    /// <summary>Referencia blanda al archivo en el módulo Storage — mismo mecanismo que
    /// <see cref="Quotation.PdfFileId"/>. Que el archivo exista, sea del tenant, ya haya
    /// terminado de subir y sea uno de los tipos aceptados (PDF/JPG/PNG, hasta 10 MB) lo valida
    /// la aplicación con <c>IQuotationFileLookup</c> antes de construir la venta.</summary>
    public Guid FileId { get; private set; }

    /// <summary>Monto que cubre este comprobante específico — cada archivo puede tener el suyo
    /// (US-14).</summary>
    public decimal Amount { get; private set; }

    public MemberId UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    internal static SalePaymentProof Create(
        SalePaymentProofId id,
        SaleId saleId,
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

        return new SalePaymentProof(id, saleId, fileId, amount, uploadedBy, uploadedAt);
    }
}
