namespace Modules.Automations.Infrastructure.Persistence;

// Spec 2026-10-21 §4.2 y §5.2: la fila de trabajo del bloque HTTP. No es un agregado: la escribe y la lee la infraestructura
// (el ejecutor y el barrido), así que vive acá y no en Domain. La simulación sí es una entidad (Domain/Simulation.cs).

/// <summary>Una llamada del bloque HTTP, de una corrida o de una simulación. El path va interpolado y sin query: la query puede
/// llevar datos del cliente.</summary>
internal sealed class HttpCallRecord
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? RunId { get; set; }

    public Guid? SimulationId { get; set; }

    public string NodeId { get; set; } = string.Empty;

    public Guid ConnectionId { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int? StatusCode { get; set; }

    public int DurationMs { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public DateTimeOffset CalledAt { get; set; }

    /// <summary>Spec 2026-10-25 D-E3: la visita del bloque (el <c>StepNumber</c> en que corrió). NULL en las filas de antes.</summary>
    public int? StepNumber { get; set; }

    /// <summary>Spec 2026-10-25 §6.1: bytes del cuerpo leído bajo el tope. NULL si no se leyó (timeout, red, demasiado grande).</summary>
    public int? ResponseBytes { get; set; }
}

/// <summary>Spec 2026-10-25 D-E5: una unidad del procesador (un entrante, un recorrido, un guardado). Se escribe en la misma
/// transacción que la corrida y se purga a los 3 días (D-E7) sin tocar <c>conversation_runs</c>.</summary>
internal sealed class RunUnitRecord
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }

    public int BaseStep { get; set; }

    public string Trigger { get; set; } = string.Empty;

    public Guid? InboundMessageId { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset EndedAt { get; set; }

    public string StepsJson { get; set; } = "[]";

    public string? ClosedStatus { get; set; }

    public string? ClosedReason { get; set; }
}
