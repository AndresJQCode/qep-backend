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

    /// <summary>§8: los hilos de <paramref name="userId"/>, sin mensajes ni tracking, por <c>(last_activity_at, id)</c> descendente, después de
    /// <paramref name="after"/>.</summary>
    Task<IReadOnlyList<CopilotThread>> ListThreadsAsync(
        Guid tenantId, Guid userId, CopilotThreadCursor? after, int take, CancellationToken cancellationToken);

    /// <summary>D-C11: los instantes de las preguntas (mensajes <c>User</c>) del miembro desde <paramref name="since"/>, en todos sus hilos.
    /// Un hilo borrado deja de contar: el tope es de abuso, no de facturación (eso lo cuida el tope mensual).</summary>
    Task<IReadOnlyList<DateTimeOffset>> ListQuestionTimesAsync(Guid tenantId, Guid userId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>D-C8: borra en una transacción los <c>tool_calls</c> de los trabajos del hilo y el hilo con sus mensajes. Los trabajos y el
    /// consumo se quedan: la cola los cierra y el consumo es lo pagado.</summary>
    Task DeleteThreadAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken);
}

public sealed record CopilotTokenUsage(long InputTokens, long OutputTokens)
{
    public long Total => InputTokens + OutputTokens;
}
