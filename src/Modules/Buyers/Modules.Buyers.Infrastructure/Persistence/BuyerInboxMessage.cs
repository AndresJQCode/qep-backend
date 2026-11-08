namespace Modules.Buyers.Infrastructure.Persistence;

/// <summary>El reclamo de un mensaje del outbox de plataforma por un consumidor de Buyers (copia de la de Notifications).</summary>
internal sealed class BuyerInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// La marca de idempotencia del alta desde el checkout: una fila por solicitud de pedido. La PK decide entre dos réplicas y entre
/// dos mensajes de la misma solicitud.
/// </summary>
internal sealed class BuyerProcessedSignup
{
    public Guid OrderRequestId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
