namespace Modules.Identity.Infrastructure.Persistence;

// Guarda de idempotencia por consumidor: un id de mensaje de outbox ya procesado por el
// consumidor de este módulo. (consumer, message_id) es único.
//
// Los dos consumidores (OrphanUserCleanupWorker y SessionRevocationWorker, por
// ClaimedOutboxConsumer) además la usan como reclamo, igual que el inbox de Notifications: una
// fila con ProcessedAt en null es un mensaje reclamado y sin terminar, ClaimedUntil es hasta cuándo
// nadie lo vuelve a tomar —su espera antes del reintento— y Attempts cuenta los reclamos
// (IdentityInboxClaims). Las filas de antes de los reclamos quedaron procesadas, con
// ClaimedUntil en null y Attempts en 1.
internal sealed class IdentityInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

// Proyección del Outbox de plataforma: la leen los consumidores de este módulo y la escribe
// IdentityOutboxWriter, en la unidad de trabajo del llamador.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public int Attempts { get; init; }
}
