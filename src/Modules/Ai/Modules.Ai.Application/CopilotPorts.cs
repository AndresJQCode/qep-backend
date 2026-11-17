using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Spec 2026-11-17 (copiloto) §5.1: la configuración y los hilos. Todo método recibe <c>tenantId</c>; los de un hilo, además, el
/// dueño: un hilo de otro miembro no se encuentra (D-C8, 404 y nunca 403).</summary>
public interface ICopilotRepository
{
    /// <summary>Con tracking; <c>null</c> si el tenant nunca guardó su configuración.</summary>
    Task<CopilotSettings?> FindSettingsAsync(Guid tenantId, CancellationToken cancellationToken);

    void AddSettings(CopilotSettings settings);

    /// <summary>Con tracking y con sus mensajes en orden; <c>null</c> si no existe en el tenant o no es de <paramref name="userId"/>.</summary>
    Task<CopilotThread?> FindThreadAsync(Guid tenantId, Guid userId, Guid threadId, CancellationToken cancellationToken);

    void AddThread(CopilotThread thread);

    /// <summary>D-C11 y D-C12: los tokens del copiloto del tenant desde <paramref name="since"/> (el comienzo del mes del tenant). Sólo filas
    /// con <c>copilot_thread_id</c>: el consumo de los agentes no cuenta.</summary>
    Task<CopilotTokenUsage> SumTokensAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed record CopilotTokenUsage(long InputTokens, long OutputTokens)
{
    public long Total => InputTokens + OutputTokens;
}
