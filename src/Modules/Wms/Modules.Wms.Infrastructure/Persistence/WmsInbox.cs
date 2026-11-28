namespace Modules.Wms.Infrastructure.Persistence;

// Inventario básico: guarda de idempotencia y reclamo por consumidor, copia de PurchasesInboxMessage (ningún módulo referencia la
// infraestructura de otro): una fila con ProcessedAt en null es un mensaje reclamado y sin terminar; ClaimedUntil es hasta cuándo
// nadie lo vuelve a tomar (su espera antes del reintento) y Attempts cuenta los reclamos.
internal sealed class WmsInboxMessage
{
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
}
