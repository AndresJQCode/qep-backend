namespace Modules.Ai.Domain;

public enum ModelJobStatus
{
    Pending,
    Running,
    Done,
    Discarded,
    Failed,
}

/// <summary>
/// Spec 2026-10-29 §4.2 y D-A13: un trabajo de modelo de la cola <c>ai.model_jobs</c>. El worker lo reclama, llama al modelo fuera de toda
/// transacción y lo cierra en un guardado corto. Es de una sesión (Tarea 7), de una prueba del playground (§7) o de un hilo del copiloto (spec
/// 2026-11-17), de uno solo.
/// </summary>
public sealed class ModelJob
{
    /// <summary>D-A17: hasta tres reintentos de un error transitorio del proveedor; el cuarto intento fallido termina en traspaso.</summary>
    public const int MaxAttempts = 4;

    /// <summary>Revisión final, hallazgo 2: tope de reclamos de un trabajo de sesión. Cada reclamo suma un intento, también los que no llaman
    /// al modelo (el candado ocupado, la carrera con Mensajería, la respuesta guardada), así que el tope es mayor que
    /// <see cref="MaxAttempts"/>. Pasado el tope, el trabajo se cierra con traspaso: uno que falla siempre (un error del código, no del
    /// proveedor) ya no se reclama para siempre.</summary>
    public const int MaxClaims = 10;

    private ModelJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? SessionId { get; private set; }

    public Guid? TestId { get; private set; }

    /// <summary>Spec 2026-11-17 (copiloto) §5.1: el tercer dueño posible, un hilo del copiloto. El <c>CHECK</c> exige exactamente uno.</summary>
    public Guid? CopilotThreadId { get; private set; }

    public int UpToSeq { get; private set; }

    public ModelJobStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? Outcome { get; private set; }

    public string? ErrorCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>§6.3 paso 4: la respuesta ya calculada cuando el commit corto no pudo tomar el candado de la conversación. El reintento la
    /// usa sin llamar al modelo otra vez. JSON propio de Application; nunca sale a logs.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Spec 2026-10-29 §6.2 paso 6/7: el trabajo de una sesión, con el debounce de D-A14 para juntar ráfagas.</summary>
    public static ModelJob ForSession(Guid id, Guid tenantId, Guid sessionId, int upToSeq, DateTimeOffset nextAttemptAt, DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            SessionId = sessionId,
            UpToSeq = upToSeq,
            Status = ModelJobStatus.Pending,
            NextAttemptAt = nextAttemptAt,
            CreatedAt = now,
        };

    /// <summary>D-A14: un entrante que llega con este trabajo todavía <c>Pending</c> sólo mueve hasta dónde responde.</summary>
    public void ExtendTo(int upToSeq)
    {
        if (Status != ModelJobStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending job takes more customer turns.");
        }

        UpToSeq = Math.Max(UpToSeq, upToSeq);
    }

    /// <summary>§6.3 paso 4: el candado de la conversación estaba tomado; la respuesta queda guardada y el trabajo se retoma en
    /// <paramref name="retryAt"/>. Sigue <c>Running</c> con el lease acortado, no vuelve a <c>Pending</c>: la sesión puede tener ya el
    /// siguiente trabajo pendiente (D-A14) y el índice único de pendientes chocaría.</summary>
    public void HoldResult(string resultJson, DateTimeOffset retryAt)
    {
        ResultJson = resultJson;
        LeaseUntil = retryAt;
    }

    /// <summary>Revisión final, hallazgo 2: la respuesta del modelo se guarda apenas vuelve, antes de enviar nada. Si el commit corto falla o
    /// el proceso muere, el trabajo retomado la usa y nunca vuelve a pagar el modelo. No toca el lease.</summary>
    public void RememberResult(string resultJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        ResultJson = resultJson;
    }

    public static ModelJob ForTest(Guid id, Guid tenantId, Guid testId, int upToSeq, DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            TestId = testId,
            UpToSeq = upToSeq,
            Status = ModelJobStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };

    /// <summary>Spec 2026-11-17 (copiloto) §7 paso 1: la respuesta a la pregunta <paramref name="upToSeq"/> de un hilo, sin debounce.</summary>
    public static ModelJob ForCopilot(Guid id, Guid tenantId, Guid threadId, int upToSeq, DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            CopilotThreadId = threadId,
            UpToSeq = upToSeq,
            Status = ModelJobStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };

    /// <summary>Un error transitorio: vuelve a la cola para <paramref name="nextAttemptAt"/>.</summary>
    public void Reschedule(DateTimeOffset nextAttemptAt, string errorCode)
    {
        Status = ModelJobStatus.Pending;
        NextAttemptAt = nextAttemptAt;
        LeaseUntil = null;
        ErrorCode = errorCode;
    }

    /// <summary>D-A17 para el trabajo de una sesión: como <see cref="Reschedule"/>, pero sigue <c>Running</c> con el lease hasta
    /// <paramref name="retryAt"/>, por la misma razón que <see cref="HoldResult"/>: el siguiente trabajo de la sesión puede estar ya pendiente.</summary>
    public void RetryAt(DateTimeOffset retryAt, string errorCode)
    {
        LeaseUntil = retryAt;
        ErrorCode = errorCode;
    }

    public void Complete(ModelJobStatus status, string outcome, string? errorCode, DateTimeOffset now)
    {
        if (status is ModelJobStatus.Pending or ModelJobStatus.Running)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A job completes as Done, Discarded or Failed.");
        }

        Status = status;
        Outcome = outcome;
        ErrorCode = errorCode;
        LeaseUntil = null;
        CompletedAt = now;
    }
}
