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
/// transacción y lo cierra en un guardado corto. Es de una sesión (Tarea 7) o de una prueba del playground (§7), nunca de las dos.
/// </summary>
public sealed class ModelJob
{
    /// <summary>D-A17: hasta tres reintentos de un error transitorio del proveedor; el cuarto intento fallido termina en traspaso.</summary>
    public const int MaxAttempts = 4;

    private ModelJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? SessionId { get; private set; }

    public Guid? TestId { get; private set; }

    public int UpToSeq { get; private set; }

    public ModelJobStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? Outcome { get; private set; }

    public string? ErrorCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

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

    /// <summary>Un error transitorio: vuelve a la cola para <paramref name="nextAttemptAt"/>.</summary>
    public void Reschedule(DateTimeOffset nextAttemptAt, string errorCode)
    {
        Status = ModelJobStatus.Pending;
        NextAttemptAt = nextAttemptAt;
        LeaseUntil = null;
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
