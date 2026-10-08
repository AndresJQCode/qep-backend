namespace Modules.Quotations.Domain;

/// <summary>
/// Un comprobante de pago adjuntado durante la conversión de una cotización en pedido (US-14), o
/// sumado después mientras el pedido sigue pendiente (<see cref="Order.AddPaymentProofs"/>).
/// Entidad hija de <see cref="Order"/>: quién lo subió no cambia una vez creado, pero el monto sí
/// puede corregirse (a pedido, 2026-09) —ver <see cref="UpdateAmount"/>— si alguien lo tipeó mal,
/// y desde 2026-09-15 también el archivo —ver <see cref="UpdateFile"/>— si el que se subió no era
/// el correcto; ahí también se reemplaza su copia pública, para que nunca quede mostrando un
/// archivo que ya no es el vigente.
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
        string? publicStorageKey,
        decimal amount,
        MemberId uploadedBy,
        DateTimeOffset uploadedAt,
        DateOnly? paidOn)
    {
        Id = id;
        OrderId = orderId;
        FileId = fileId;
        PublicStorageKey = publicStorageKey;
        Amount = amount;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
        PaidOn = paidOn;
    }

    public OrderPaymentProofId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    /// <summary>Referencia blanda al archivo en el módulo Storage — mismo mecanismo que
    /// <see cref="Quotation.PdfFileId"/>. Que el archivo exista, sea del tenant, ya haya
    /// terminado de subir y sea uno de los tipos aceptados (PDF/JPG/PNG, hasta 10 MB) lo valida
    /// la aplicación con <c>IQuotationFileLookup</c> antes de construir el pedido.</summary>
    public Guid FileId { get; private set; }

    /// <summary>La clave de la copia del archivo en el bucket público de R2
    /// (<c>payment-proofs/{guid}.{ext}</c>), o null si el comprobante es privado: se adjuntó con
    /// <c>Quotations:PaymentProofs:PublicLinks</c> apagada, o antes de que la opción existiera
    /// (spec 2026-09-15, P5 y P8). Se guarda la clave y no la URL: la URL se arma al exportar con
    /// el dominio público vigente, así que cambiar el dominio no rompe nada. Cambia junto con
    /// <see cref="FileId"/> si el archivo se reemplaza (2026-09-15) — ver
    /// <see cref="UpdateFile"/>.</summary>
    public string? PublicStorageKey { get; private set; }

    /// <summary>Monto que cubre este comprobante específico — cada archivo puede tener el suyo
    /// (US-14).</summary>
    public decimal Amount { get; private set; }

    public MemberId UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>La fecha del soporte de pago, tal como la escribe quien adjunta el comprobante (a
    /// pedido, 2026-10-08): el día en que el cliente pagó, que no es el día en que se subió el
    /// archivo (<see cref="UploadedAt"/>) y es la que el Excel de pedidos muestra en «Fecha Pago N».
    /// Fecha y no instante, porque el soporte no trae hora. Null en los comprobantes anteriores al
    /// campo y en los que se adjuntan sin ella: no se inventa a partir de la subida.</summary>
    public DateOnly? PaidOn { get; private set; }

    internal static OrderPaymentProof Create(
        OrderPaymentProofId id,
        OrderId orderId,
        Guid fileId,
        string? publicStorageKey,
        decimal amount,
        MemberId uploadedBy,
        DateTimeOffset uploadedAt,
        DateOnly? paidOn)
    {
        if (fileId == Guid.Empty)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_required",
                "The payment proof file is required.");
        }

        if (amount <= 0)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.amount_invalid",
                "The payment proof amount must be greater than zero.");
        }

        return new OrderPaymentProof(
            id, orderId, fileId, publicStorageKey, amount, uploadedBy, uploadedAt, paidOn);
    }

    /// <summary>Corrige el monto de un comprobante ya cargado (a pedido, 2026-09) — sólo desde
    /// <see cref="Order.AddPaymentProofs"/>, que es quien decide si el pedido admite el cambio
    /// (sólo <see cref="OrderStatus.Pending"/>). El archivo, su copia pública y quién lo subió no
    /// se tocan: es una corrección puntual del importe, no otro comprobante.</summary>
    internal void UpdateAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.amount_invalid",
                "The payment proof amount must be greater than zero.");
        }

        Amount = amount;
    }

    /// <summary>Corrige la fecha del soporte de un comprobante ya cargado (a pedido, 2026-10-08):
    /// la pantalla de edición la precarga junto al monto y manda las dos, así que lo que llega
    /// reemplaza lo guardado y null la borra — mismo criterio que <see cref="UpdateAmount"/> y que
    /// <c>Order.Notes</c>, no el de <see cref="UpdateFile"/>, donde null conserva el archivo. Sin
    /// validación: la fecha es la que escribió quien adjuntó, ver <see cref="PaidOn"/>.</summary>
    internal void UpdatePaidOn(DateOnly? paidOn)
    {
        PaidOn = paidOn;
    }

    /// <summary>Reemplaza el archivo de un comprobante ya cargado, y su copia pública con él (a
    /// pedido, 2026-09-15) — sólo desde <see cref="Order.AddPaymentProofs"/>, que ya validó contra
    /// Storage que el archivo nuevo existe, es del tenant y terminó de subir (mismo chequeo que un
    /// comprobante nuevo), y que ya publicó <paramref name="publicStorageKey"/> si la opción de
    /// enlaces públicos está prendida. Ni el archivo viejo ni su copia pública vieja se borran acá
    /// —el caller las borra después de guardar, best-effort, igual que un comprobante descartado
    /// sin guardar en el asistente— para no dejar el pedido sin archivo si el borrado falla.
    /// </summary>
    internal void UpdateFile(Guid fileId, string? publicStorageKey)
    {
        if (fileId == Guid.Empty)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_required",
                "The payment proof file is required.");
        }

        FileId = fileId;
        PublicStorageKey = publicStorageKey;
    }
}
