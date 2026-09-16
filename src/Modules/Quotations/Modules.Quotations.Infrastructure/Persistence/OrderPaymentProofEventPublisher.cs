using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

// Acumula el evento en la proyección de outbox de QuotationsDbContext, para que commitee en la misma
// transacción que el pedido (spec 2026-09-16, D9). Lo consume PaymentProofMoveWorker, en Storage, que
// lee el payload por nombre: los campos son contrato.
internal sealed class OrderPaymentProofEventPublisher(QuotationsDbContext dbContext)
    : IOrderPaymentProofEventPublisher
{
    internal const string AttachedEventName = "quotations.order.payment-proofs-attached.v1";

    public void PublishAttached(
        Guid tenantId,
        OrderId orderId,
        IReadOnlyCollection<AttachedPaymentProof> proofs,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = AttachedEventName,
            PayloadJson = JsonSerializer.Serialize(new AttachedPayload(
                tenantId,
                orderId.Value,
                proofs
                    .Select(proof => new AttachedProofPayload(proof.FileId, proof.PublicStorageKey))
                    .ToArray())),
            // La correlación es el pedido: soporte llega del log del worker de Storage a la fila de
            // orders sin adivinar.
            CorrelationId = orderId.Value.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por nombre
    // con JsonDocument.
    private sealed record AttachedPayload(
        Guid tenantId,
        Guid orderId,
        IReadOnlyCollection<AttachedProofPayload> proofs);

    private sealed record AttachedProofPayload(Guid fileId, string publicStorageKey);
}
