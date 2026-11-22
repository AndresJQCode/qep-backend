namespace Modules.Payroll.Domain;

/// <summary>
/// Una quincena de nómina de una empresa y su máquina de estados (contrato HTTP, «Rutas»). Una sola versión, <see cref="Version"/>,
/// que sube con <b>toda</b> escritura (R-17): el <c>If-Match</c> de capturas, liquidar, aprobar, cerrar y reabrir. Un solo método
/// público = una sola subida, aunque marque el período desactualizado de paso.
/// </summary>
public sealed class PayrollPeriod
{
    private readonly List<PeriodInput> _inputs = [];
    private readonly List<PeriodEvent> _events = [];

    private PayrollPeriod()
    {
    }

    public PayrollPeriodId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>FK real a <c>companies.companies</c>, agregada a mano en la migración (R-19): fija desde que se abre.</summary>
    public Guid CompanyId { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    public PayrollHalf Half { get; private set; }

    public PayrollPeriodStatus Status { get; private set; }

    /// <summary>Una captura o la primera quincena cambió después de liquidar: hay que volver a liquidar antes de aprobar.</summary>
    public bool Outdated { get; private set; }

    public long Version { get; private set; }

    /// <summary>La foto legal de la primera liquidación (JSON de <c>LegalValueSet</c>): no cambia aunque Hr cambie.</summary>
    public string? LegalValuesJson { get; private set; }

    public DateOnly? LegalValuesDate { get; private set; }

    public string? EngineVersion { get; private set; }

    /// <summary>La huella del conjunto de la última liquidación (R-16).</summary>
    public string? Fingerprint { get; private set; }

    /// <summary>Los excluidos de la última liquidación, con su motivo (JSON): en Aprobado y Cerrado son lo que se muestra.</summary>
    public string? ExcludedJson { get; private set; }

    public Guid? LastLiquidatedBy { get; private set; }

    public string? LastLiquidatedByName { get; private set; }

    public DateTimeOffset? LastLiquidatedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public string? ApprovedByName { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public string? ClosedByName { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<PeriodInput> Inputs => _inputs;

    public IReadOnlyList<PeriodEvent> Events => _events;

    public static PayrollPeriod Open(
        PayrollPeriodId id,
        Guid tenantId,
        Guid companyId,
        (DateOnly Start, DateOnly End, PayrollHalf Half) quincena,
        PayrollActor actor,
        DateTimeOffset now)
    {
        var period = new PayrollPeriod
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            StartDate = quincena.Start,
            EndDate = quincena.End,
            Half = quincena.Half,
            Status = PayrollPeriodStatus.Draft,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        period.AddEvent(actor, PeriodAction.Created, null, PayrollPeriodStatus.Draft, null, null, now);
        return period;
    }

    /// <summary>
    /// Reemplaza las capturas de un empleado. Sólo en Borrador, Liquidado y Reabierto; sobre un Liquidado deja el período
    /// desactualizado (y un solo evento mientras siga así). Una captura manual no deja evento en el historial: quedaría uno por cada
    /// guardado (llamada del plan); sí lo deja el desactualizado.
    /// </summary>
    public void ReplaceInputs(Guid employeeId, IReadOnlyList<PeriodInputDraft> lines, PayrollActor actor, DateTimeOffset now)
    {
        EnsureEditable();
        ReplaceFor(employeeId, lines);
        MarkOutdatedAfterEdit(actor, now);
        Touch(now);
    }

    /// <summary>La importación confirmada: reemplaza lo de cada empleado del archivo (R-24), una sola versión y un evento <c>InputsImported</c>.</summary>
    public void ReplaceInputsBulk(
        IReadOnlyDictionary<Guid, IReadOnlyList<PeriodInputDraft>> linesByEmployee, PayrollActor actor, DateTimeOffset now)
    {
        EnsureEditable();
        foreach (var (employeeId, lines) in linesByEmployee)
        {
            ReplaceFor(employeeId, lines);
        }

        AddEvent(actor, PeriodAction.InputsImported, Status, Status, null, null, now);
        MarkOutdatedAfterEdit(actor, now);
        Touch(now);
    }

    /// <summary>
    /// La primera quincena cambió (R-14) o algo más desactualizó el período. Sólo si no es un Borrador y no lo estaba ya; si no, no
    /// hace nada (ni sube la versión).
    /// </summary>
    public void MarkOutdated(PayrollActor actor, DateTimeOffset now)
    {
        if (Status == PayrollPeriodStatus.Draft || Outdated)
        {
            return;
        }

        MarkOutdatedCore(actor, now);
        Touch(now);
    }

    /// <summary>
    /// Registra una liquidación: desde Borrador, Reabierto o Liquidado. La foto legal se fija <b>sólo la primera vez</b>: reabrir y
    /// volver a liquidar conserva los montos aunque Hr tenga valores nuevos (Review Focus 3).
    /// </summary>
    public void RecordLiquidation(
        PayrollActor actor,
        bool absencesConfirmed,
        string legalValuesJson,
        DateOnly legalValuesDate,
        string engineVersion,
        string fingerprint,
        string excludedJson,
        DateTimeOffset now)
    {
        if (Status is not (PayrollPeriodStatus.Draft or PayrollPeriodStatus.Reopened or PayrollPeriodStatus.Liquidated))
        {
            throw InvalidTransition();
        }

        if (!absencesConfirmed)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.LiquidationAbsencesUnconfirmed, "The absences of the period must be confirmed before liquidating.");
        }

        var from = Status;
        LegalValuesJson ??= legalValuesJson;
        LegalValuesDate ??= legalValuesDate;
        EngineVersion = engineVersion;
        Fingerprint = fingerprint;
        ExcludedJson = excludedJson;
        Outdated = false;
        Status = PayrollPeriodStatus.Liquidated;
        LastLiquidatedBy = actor.SubjectId;
        LastLiquidatedByName = actor.DisplayName;
        LastLiquidatedAt = now;
        AddEvent(actor, PeriodAction.Liquidated, from, PayrollPeriodStatus.Liquidated, null, true, now);
        Touch(now);
    }

    /// <summary>Los códigos de <c>approveBlockedBy</c>, en el orden del contrato; vacío si se puede aprobar.</summary>
    public IReadOnlyList<string> ApproveBlockers(ApprovalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var blockers = new List<string>(6);
        if (Status != PayrollPeriodStatus.Liquidated)
        {
            blockers.Add(ApproveBlocker.Status);
        }

        if (Outdated)
        {
            blockers.Add(ApproveBlocker.Outdated);
        }

        if (context.SourceChanged)
        {
            blockers.Add(ApproveBlocker.SourceChanged);
        }

        if (context.AnyNegativeNet)
        {
            blockers.Add(ApproveBlocker.NegativeNet);
        }

        if (Half == PayrollHalf.Second && !context.FirstHalfApproved)
        {
            blockers.Add(ApproveBlocker.FirstHalfNotApproved);
        }

        if (context.DualControl && LastLiquidatedBy == context.Caller.SubjectId)
        {
            blockers.Add(ApproveBlocker.DualControl);
        }

        return blockers;
    }

    /// <summary>Aprueba el período; si algo lo impide lanza el primer bloqueo con su código.</summary>
    public void Approve(ApprovalContext context, DateTimeOffset now)
    {
        var blockers = ApproveBlockers(context);
        if (blockers.Count > 0)
        {
            throw blockers[0] switch
            {
                ApproveBlocker.Status => InvalidTransition(),
                ApproveBlocker.Outdated or ApproveBlocker.SourceChanged => new PayrollDomainException(
                    PayrollErrorCodes.PeriodOutdated, "The period changed after it was liquidated: liquidate it again."),
                ApproveBlocker.NegativeNet => new PayrollDomainException(
                    PayrollErrorCodes.PeriodNegativeNet, "An employee has a negative net pay."),
                ApproveBlocker.FirstHalfNotApproved => new PayrollDomainException(
                    PayrollErrorCodes.PeriodFirstHalfNotApproved, "The first half of the month must be approved first."),
                _ => new PayrollDomainException(
                    PayrollErrorCodes.PeriodSamePersonCannotApprove, "The person who liquidated the period cannot approve it."),
            };
        }

        Status = PayrollPeriodStatus.Approved;
        ApprovedBy = context.Caller.SubjectId;
        ApprovedByName = context.Caller.DisplayName;
        ApprovedAt = now;
        AddEvent(context.Caller, PeriodAction.Approved, PayrollPeriodStatus.Liquidated, PayrollPeriodStatus.Approved, null, null, now);
        Touch(now);
    }

    /// <summary>Cierra un período aprobado y al día.</summary>
    public void Close(PayrollActor actor, DateTimeOffset now)
    {
        if (Status != PayrollPeriodStatus.Approved)
        {
            throw InvalidTransition();
        }

        if (Outdated)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.PeriodOutdated, "The period changed after it was liquidated: reopen it and liquidate it again.");
        }

        Status = PayrollPeriodStatus.Closed;
        ClosedBy = actor.SubjectId;
        ClosedByName = actor.DisplayName;
        ClosedAt = now;
        AddEvent(actor, PeriodAction.Closed, PayrollPeriodStatus.Approved, PayrollPeriodStatus.Closed, null, null, now);
        Touch(now);
    }

    /// <summary>
    /// Reabre un período aprobado o cerrado, con el motivo. Ya no está aprobado ni cerrado, así que esas marcas se limpian (el
    /// historial las conserva); el resultado guardado sigue hasta la próxima liquidación.
    /// </summary>
    public void Reopen(PayrollActor actor, string reason, DateTimeOffset now)
    {
        if (Status is not (PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed))
        {
            throw InvalidTransition();
        }

        var from = Status;
        Status = PayrollPeriodStatus.Reopened;
        ApprovedBy = null;
        ApprovedByName = null;
        ApprovedAt = null;
        ClosedBy = null;
        ClosedByName = null;
        ClosedAt = null;
        AddEvent(actor, PeriodAction.Reopened, from, PayrollPeriodStatus.Reopened, reason, null, now);
        Touch(now);
    }

    /// <summary>Sólo un período Borrador, Liquidado o Reabierto recibe capturas; Aprobado y Cerrado se reabren primero.</summary>
    public void EnsureEditable()
    {
        if (Status is PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.PeriodNotEditable, "An approved or closed period cannot be edited: reopen it first.");
        }
    }

    private void ReplaceFor(Guid employeeId, IReadOnlyList<PeriodInputDraft> lines)
    {
        _inputs.RemoveAll(input => input.EmployeeId == employeeId);
        var positions = new Dictionary<ConceptKind, int>();
        foreach (var line in lines)
        {
            var position = positions.GetValueOrDefault(line.Kind);
            positions[line.Kind] = position + 1;
            _inputs.Add(new PeriodInput(Id, employeeId, line, position));
        }
    }

    // Una captura sobre un Liquidado deja el período desactualizado; en Borrador y Reabierto todavía no hay nada que desactualizar.
    private void MarkOutdatedAfterEdit(PayrollActor actor, DateTimeOffset now)
    {
        if (Status == PayrollPeriodStatus.Liquidated && !Outdated)
        {
            MarkOutdatedCore(actor, now);
        }
    }

    private void MarkOutdatedCore(PayrollActor actor, DateTimeOffset now)
    {
        Outdated = true;
        AddEvent(actor, PeriodAction.MarkedOutdated, Status, Status, null, null, now);
    }

    private void AddEvent(
        PayrollActor actor,
        PeriodAction action,
        PayrollPeriodStatus? from,
        PayrollPeriodStatus? to,
        string? reason,
        bool? absencesConfirmed,
        DateTimeOffset now) =>
        _events.Add(new PeriodEvent(
            Id, (_events.Count == 0 ? 0 : _events.Max(evt => evt.Sequence)) + 1, now, actor, action, from, to, reason, absencesConfirmed));

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private static PayrollDomainException InvalidTransition() =>
        new(PayrollErrorCodes.PeriodInvalidTransition, "The period cannot make that transition from its current status.");
}

/// <summary>Los códigos de <c>approveBlockedBy</c> del contrato HTTP.</summary>
public static class ApproveBlocker
{
    public const string Status = "status";
    public const string Outdated = "outdated";
    public const string SourceChanged = "source_changed";
    public const string NegativeNet = "negative_net";
    public const string FirstHalfNotApproved = "first_half_not_approved";
    public const string DualControl = "dual_control";
}
