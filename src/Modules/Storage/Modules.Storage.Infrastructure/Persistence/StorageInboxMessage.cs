namespace Modules.Storage.Infrastructure.Persistence;

// Guarda de idempotencia por consumidor (spec 2026-09-16, D9): un mensaje del outbox de plataforma
// que un consumidor de Storage ya procesó. (consumer, message_id) es única. Mismo diseño que
// IdentityInboxMessage.
// Spec 2026-11-26 («Consumidor»): también es el reclamo de StorageInboxClaims. Una fila con ProcessedAt en null es un mensaje
// reclamado y sin terminar; ClaimedUntil es hasta cuándo nadie lo vuelve a tomar (su espera antes del reintento) y Attempts cuenta
// los reclamos. Los consumidores que no reclaman insertan la fila ya terminada y toman attempts = 1 por el default de la columna.
internal sealed class StorageInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}
