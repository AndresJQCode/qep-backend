namespace Modules.Identity.Infrastructure.Persistence;

// Guarda de idempotencia por consumidor: un id de mensaje de outbox ya procesado por el
// consumidor de este módulo. (consumer, message_id) es único.
//
// OrphanUserCleanupWorker además la usa como reclamo, igual que el inbox de Notifications: una
// fila con ProcessedAt en null es un mensaje reclamado y sin terminar, ClaimedUntil es hasta cuándo
// nadie lo vuelve a tomar —su espera antes del reintento— y Attempts cuenta los reclamos
// (IdentityInboxClaims). SessionRevocationWorker no reclama: escribe la fila ya procesada, y
// ClaimedUntil le queda en null y Attempts en 1.
internal sealed class IdentityInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}

// Proyección de sólo lectura del Outbox de plataforma, consumida independiente por este módulo.
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; }
}
