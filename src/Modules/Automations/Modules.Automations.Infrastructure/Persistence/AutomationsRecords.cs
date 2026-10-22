namespace Modules.Automations.Infrastructure.Persistence;

// Spec 2026-10-21 §4.2 y §5.2: filas de trabajo del motor, del bloque HTTP y del simulador. No son agregados: las escribe y las
// lee la infraestructura (el worker del plan 2), así que viven acá y no en Domain.

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
}

/// <summary>Spec 2026-10-21 §8: una sesión del simulador del editor, con su estado del motor y la conversación de mentira. Vence
/// (<c>ExpiresAt</c>) y no deja rastro en Mensajería.</summary>
internal sealed class SimulationRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ChatbotId { get; set; }

    public Guid MemberId { get; set; }

    public bool LiveHttp { get; set; }

    public string StateJson { get; set; } = "{}";

    public string TranscriptJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
