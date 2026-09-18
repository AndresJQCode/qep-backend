using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Avisa que un pedido acaba de adjuntar comprobantes con copia pública (spec 2026-09-16, D9). Lo
/// consume Storage (<c>PaymentProofMoveWorker</c>) para borrar el temporal y registrar el
/// movimiento. Se escribe en el outbox de la misma unidad de trabajo que el pedido: si guardar
/// falla, el evento tampoco existe. Mismo mecanismo que <see cref="IExportEventPublisher"/>.
/// </summary>
public interface IOrderPaymentProofEventPublisher
{
    /// <summary><c>quotations.order.payment-proofs-attached.v1</c>.</summary>
    void PublishAttached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<AttachedPaymentProof> proofs,
        DateTimeOffset occurredAt);

    /// <summary><c>quotations.order.payment-proofs-detached.v1</c> (spec 2026-09-16, D19): los adjuntos
    /// que el pedido soltó al reemplazar o quitar comprobantes (ver
    /// <see cref="PaymentProofCopies.DetachedFrom"/>). Lo consume <c>PaymentProofDetachProcessor</c>, en
    /// Storage, que borra la copia de cada uno y purga el archivo si nadie lo retiene.</summary>
    void PublishDetached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<DetachedPaymentProof> proofs,
        DateTimeOffset occurredAt);
}

/// <summary>Un comprobante recién adjuntado y la clave de su copia en el bucket público.</summary>
public sealed record AttachedPaymentProof(Guid FileId, string PublicStorageKey);

/// <summary>Un adjunto que el pedido soltó: su archivo y la clave de la copia pública que tenía ese
/// comprobante, o null si no tenía (la opción apagada, D19).</summary>
public sealed record DetachedPaymentProof(Guid FileId, string? PublicStorageKey);
