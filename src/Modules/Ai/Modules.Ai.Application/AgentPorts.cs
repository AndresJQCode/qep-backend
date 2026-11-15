using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

public interface IAgentRepository
{
    /// <summary>El agente con sus conexiones y versiones, sólo si es del tenant.</summary>
    Task<Agent?> FindAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>Los agentes del tenant ordenados por nombre; <paramref name="search"/> filtra por nombre, sin importar mayúsculas.</summary>
    Task<IReadOnlyList<Agent>> ListAsync(Guid tenantId, string? search, CancellationToken cancellationToken);

    Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken);

    void Add(Agent agent);

    void Remove(Agent agent);
}

/// <summary>Copia de <c>IAutomationsUnitOfWork</c>: nombre repetido → 409 <c>ai.agent.name_taken</c>, versión vieja → 412. Si el guardado
/// toca un agente o sus conexiones, la ocupación neutral de Mensajería se sincroniza en la misma transacción (§4.3), y un bot que ya ocupa
/// la conexión vuelve como 409 <c>ai.agent.connection_taken</c> sin dejar nada escrito.</summary>
public interface IAiUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Una conexión de Mensajería del tenant, en cualquier estado. <see cref="Handle"/> es el número o el usuario visible; vacío si
/// la conexión no está activa.</summary>
public sealed record AiConnectionRef(Guid Id, string Name, string Channel, string Handle, string Status);

/// <summary>Las conexiones de Mensajería del tenant (adaptador en Bootstrapper): Ai no referencia Messaging ni Integrations (D-A3).</summary>
public interface IAiConnections
{
    Task<IReadOnlyList<AiConnectionRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Sólo los ids de las conexiones que siguen existiendo, en cualquier estado: lo que necesita validar el borrador.</summary>
    Task<IReadOnlySet<Guid>> ListIdsAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Un miembro que puede responder conversaciones: el destino posible de un traspaso.</summary>
public sealed record AiMemberRef(Guid Id, string Name);

/// <summary>Los miembros activos con <c>messaging.conversation.manage</c>, los mismos que ofrece «Transferir» (adaptador en Bootstrapper).</summary>
public interface IAiMembers
{
    Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Quién ocupa una conexión en <c>messaging.connection_bots</c> (§4.3): <see cref="Kind"/> es <see cref="Chatbot"/> o
/// <see cref="Agent"/>, en minúscula como lo lee el frontend.</summary>
public sealed record AiConnectionOccupant(Guid ConnectionId, string Kind, Guid Id, string Name)
{
    public const string Chatbot = "chatbot";
    public const string Agent = "agent";
}

/// <summary>
/// Spec 2026-10-29 §4.3: la ocupación neutral de las conexiones, que es de Mensajería y se escribe con SQL desde Bootstrapper. Ai sólo ve
/// este puerto. <see cref="SyncAgentsAsync"/> corre dentro de la transacción de <see cref="IAiUnitOfWork"/>.
/// </summary>
public interface IAgentConnectionSlots
{
    /// <summary>Deja <c>connection_bots</c> igual a los agentes publicados (activos o en pausa) del tenant y devuelve las conexiones de
    /// esos agentes que ocupa otro bot, sin pisarlas.</summary>
    Task<IReadOnlyList<AiConnectionOccupant>> SyncAgentsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Quién ocupa cada conexión del tenant, sea chatbot o agente.</summary>
    Task<IReadOnlyList<AiConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>El camino atómico de auditoría (ADR 0019), como <c>IAutomationsAuditRecorder</c>: la entrada viaja en el mismo commit.</summary>
public interface IAiAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, string action, Guid agentId, DateTimeOffset at);
}

/// <summary>Spec 2026-10-29 §4.7: las acciones de auditoría de los agentes.</summary>
public static class AiAuditActions
{
    public const string ResourceAgent = "ai_agent";
    public const string AgentCreated = "ai.agent.created";
    public const string AgentUpdated = "ai.agent.updated";
    public const string AgentDraftSaved = "ai.agent.draft_saved";
    public const string AgentPublished = "ai.agent.published";
    public const string AgentPaused = "ai.agent.paused";
    public const string AgentResumed = "ai.agent.resumed";
    public const string AgentDeleted = "ai.agent.deleted";
}

/// <summary>§4.3: el 409 <c>ai.agent.connection_taken</c> lleva <c>occupant</c> para que la pantalla ofrezca ir a pausar al que la ocupa.</summary>
public sealed class AgentConnectionTakenException(AiConnectionOccupant occupant, Exception? innerException = null)
    : ResourceConflictException(
        AiErrorCodes.AgentConnectionTaken,
        occupant.Kind == AiConnectionOccupant.Agent
            ? $"El agente IA «{occupant.Name}» ya atiende esa conexión."
            : $"El chatbot «{occupant.Name}» ya atiende esa conexión.",
        innerException),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["occupant"] = new { kind = occupant.Kind, id = occupant.Id, name = occupant.Name },
    };
}

/// <summary>Plan 2026-11-12 C-18: 422 con el proveedor que falta, para que la pantalla lleve a conectarlo.</summary>
public sealed class AiProviderNotConfiguredException(AiProvider provider)
    : DomainException(
        AiErrorCodes.ProviderNotConfigured,
        "Conecta la cuenta del proveedor del modelo en Integraciones para publicar el agente."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["provider"] = provider.Value,
    };
}
