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

    /// <summary>D-C11 y revisión de la rama, hallazgo 4: los instantes de las preguntas del miembro desde <paramref name="since"/>, del
    /// registro <c>ai.copilot_questions</c> y no de los mensajes: borrar un hilo no devuelve el cupo (antes sí, y el tope se esquivaba
    /// borrando).</summary>
    Task<IReadOnlyList<DateTimeOffset>> ListQuestionTimesAsync(Guid tenantId, Guid userId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Hallazgo 4: abre la transacción (si no hay una) y toma el candado del miembro (<c>pg_advisory_xact_lock</c> de dos claves,
    /// espacio propio) antes de contar sus preguntas. Dos preguntas paralelas del mismo miembro hacen fila: la segunda cuenta la primera.
    /// Se suelta con <see cref="ICopilotAskingLock.CommitAsync"/> o, sin commit, al disponerse (rollback).</summary>
    Task<ICopilotAskingLock> LockAskingAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Hallazgo 4: anota la pregunta en el registro del miembro; sólo agrega al contexto y viaja en el guardado de la pregunta. Sin
    /// texto (D-C13): tenant, miembro e instante.</summary>
    void AddQuestion(Guid tenantId, Guid userId, DateTimeOffset askedAt);

    /// <summary>Hallazgo 4: el registro sólo sirve para el tope del día; el barrido borra lo anterior a <paramref name="before"/> y devuelve
    /// cuántas filas borró.</summary>
    Task<int> DeleteQuestionsBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>D-C8: borra en una transacción los <c>tool_calls</c> de los trabajos del hilo y el hilo con sus mensajes. Los trabajos y el
    /// consumo se quedan: la cola los cierra y el consumo es lo pagado. D-C13: en esa misma transacción guarda lo que haya pendiente en la
    /// unidad de trabajo (la entrada <c>ai.copilot.thread_deleted</c>): sin borrado no hay auditoría, y sin auditoría no hay borrado.</summary>
    Task DeleteThreadAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken);

    /// <summary>D-C12, plan C3.4: los tokens del copiloto del tenant desde <paramref name="since"/>, por miembro que preguntó
    /// (<c>member_user_id</c>). Incluye el consumo de hilos ya borrados: es lo pagado.</summary>
    Task<IReadOnlyList<CopilotMemberTokenUsage>> SumTokensByMemberAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>D-C8: hasta <paramref name="take"/> hilos, de todos los tenants, sin actividad en la retención de su tenant
    /// (<c>retention_days</c>, o <see cref="CopilotSettings.DefaultRetentionDays"/> sin configuración), los más viejos primero.</summary>
    Task<IReadOnlyList<CopilotExpiredThread>> ListExpiredThreadsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>D-C9 (plan fase 2, Parte C): borra los <c>tool_calls</c> de trabajos del copiloto creados antes de <paramref name="before"/>.
    /// Los de los agentes no se tocan (P-5 sigue abierta). Devuelve cuántos borró.</summary>
    Task<int> DeleteToolCallsBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>§7 paso 2: la lectura de la etapa larga del worker, sin tracking ni dueño (el trabajo ya es del hilo). <c>null</c> si se
    /// borró.</summary>
    Task<CopilotThread?> ReadThreadAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken);

    /// <summary>§7 paso 3: la misma lectura con tracking, para el commit corto; trae el estado de la base (también el <c>progress</c> que
    /// escribió <see cref="SetProgressAsync"/>).</summary>
    Task<CopilotThread?> FindThreadForJobAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken);

    /// <summary>D-C7: la herramienta en curso en la respuesta pendiente del trabajo, con un UPDATE corto propio, sin el change tracker ni
    /// candados de otras tablas. Si la respuesta ya no está pendiente no toca nada.</summary>
    Task SetProgressAsync(Guid tenantId, Guid threadId, Guid jobId, string progress, CancellationToken cancellationToken);
}

/// <summary>Hallazgo 4: el candado de preguntas de un miembro, vivo hasta el commit de la transacción que lo tomó.</summary>
public interface ICopilotAskingLock : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public sealed record CopilotTokenUsage(long InputTokens, long OutputTokens)
{
    public long Total => InputTokens + OutputTokens;
}

public sealed record CopilotMemberTokenUsage(Guid UserId, long InputTokens, long OutputTokens);

public sealed record CopilotExpiredThread(Guid TenantId, Guid ThreadId);

/// <summary>D-C12 y C-12: el nombre de los miembros por id de usuario (<c>Membership.DisplayName</c> o, sin él, el correo), como las notas de
/// clientes. Adaptador en Bootstrapper: Ai no ve Tenancy ni Identity. Quien ya no tiene membresía ni usuario no viene en el diccionario.</summary>
public interface ICopilotMemberNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
