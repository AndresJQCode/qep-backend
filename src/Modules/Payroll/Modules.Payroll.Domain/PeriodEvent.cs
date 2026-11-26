namespace Modules.Payroll.Domain;

/// <summary>Una entrada del historial del período: quién, cuándo, qué, desde y hacia qué estado, y el motivo o la confirmación de ausencias.</summary>
public sealed class PeriodEvent
{
    private PeriodEvent()
    {
    }

    internal PeriodEvent(
        PayrollPeriodId periodId,
        int sequence,
        DateTimeOffset occurredAt,
        PayrollActor actor,
        PeriodAction action,
        PayrollPeriodStatus? fromStatus,
        PayrollPeriodStatus? toStatus,
        string? reason,
        bool? absencesConfirmed)
    {
        Id = PeriodEventId.New();
        PeriodId = periodId;
        Sequence = sequence;
        OccurredAt = occurredAt;
        ActorSubjectId = actor.SubjectId;
        ActorName = actor.DisplayName;
        Action = action;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Reason = reason;
        AbsencesConfirmed = absencesConfirmed;
    }

    public PeriodEventId Id { get; private set; }

    public PayrollPeriodId PeriodId { get; private set; }

    /// <summary>
    /// El orden dentro del período, desde 1. Dos eventos de una misma operación comparten <see cref="OccurredAt"/> y el Guid v7 no
    /// es monótono dentro de un milisegundo: el historial desempata por acá (revisión M-3).
    /// </summary>
    public int Sequence { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid ActorSubjectId { get; private set; }

    public string ActorName { get; private set; } = string.Empty;

    public PeriodAction Action { get; private set; }

    public PayrollPeriodStatus? FromStatus { get; private set; }

    public PayrollPeriodStatus? ToStatus { get; private set; }

    /// <summary>Obligatorio al reabrir.</summary>
    public string? Reason { get; private set; }

    /// <summary>Sólo al liquidar: la confirmación de que no hay ausencias (R-27).</summary>
    public bool? AbsencesConfirmed { get; private set; }
}
