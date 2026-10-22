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
}
