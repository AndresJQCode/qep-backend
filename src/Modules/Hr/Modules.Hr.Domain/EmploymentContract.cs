namespace Modules.Hr.Domain;

/// <summary>
/// Un contrato de un empleado y sus tres historiales (salario, cargo y lugar, prórrogas). Sólo lo mueve <see cref="Employee"/>:
/// los métodos de escritura son <c>internal</c> para que la versión del empleado suba con cada cambio.
/// </summary>
public sealed class EmploymentContract
{
    private readonly List<SalaryRecord> _salaries = [];
    private readonly List<AssignmentRecord> _assignments = [];
    private readonly List<ContractRenewal> _renewals = [];

    private EmploymentContract()
    {
    }

    public EmploymentContractId Id { get; private set; }

    public EmployeeId EmployeeId { get; private set; }

    /// <summary>La empresa empleadora: fija. Cambiar de empresa es terminar y reincorporar, nunca editar el contrato.</summary>
    public Guid CompanyId { get; private set; }

    public ContractType ContractType { get; private set; }

    public ContractDuration Duration { get; private set; }

    public SalaryType SalaryType { get; private set; }

    public DateOnly StartDate { get; private set; }

    /// <summary>El fin vigente (el de la última prórroga); estimado en <c>WorkOrLabor</c>; nulo en <c>Indefinite</c>.</summary>
    public DateOnly? EndDate { get; private set; }

    public ContractStatus Status { get; private set; }

    public DateOnly? EndedOn { get; private set; }

    public DateOnly? ConvertedToIndefiniteOn { get; private set; }

    public EmploymentContractId? PreviousContractId { get; private set; }

    public Guid? DocumentFileId { get; private set; }

    public string? DocumentFileName { get; private set; }

    public TerminationReason? TerminationReason { get; private set; }

    public string? TerminationNotes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<SalaryRecord> Salaries => _salaries;

    public IReadOnlyList<AssignmentRecord> Assignments => _assignments;

    public IReadOnlyList<ContractRenewal> Renewals => _renewals;

    internal static EmploymentContract Create(
        EmployeeId employeeId,
        NewContract next,
        EmploymentContractId? previousContractId,
        bool belowMinimumAcknowledged,
        DateTimeOffset now)
    {
        var terms = next.Terms;
        var contract = new EmploymentContract
        {
            Id = next.Id,
            EmployeeId = employeeId,
            CompanyId = terms.CompanyId,
            ContractType = terms.ContractType,
            Duration = terms.Duration,
            SalaryType = terms.SalaryType,
            StartDate = terms.StartDate,
            EndDate = terms.EndDate,
            Status = ContractStatus.Active,
            PreviousContractId = previousContractId,
            DocumentFileId = next.Document?.FileId,
            DocumentFileName = next.Document?.FileName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        contract._salaries.Add(new SalaryRecord(
            next.SalaryId, next.Id, terms.StartDate, next.Amount, next.Teleworker, belowMinimumAcknowledged, null, now));
        contract._assignments.Add(new AssignmentRecord(
            next.AssignmentId, next.Id, terms.StartDate, next.PositionId, next.AreaId, next.SiteId, next.WorkplaceId, null, now));
        return contract;
    }

    internal void EnsureActive()
    {
        if (Status != ContractStatus.Active)
        {
            throw new HrDomainException(HrErrorCodes.ContractNotActive, "The contract is not active.");
        }
    }

    internal void EnsureFixedTerm()
    {
        if (Duration != ContractDuration.FixedTerm)
        {
            throw new HrDomainException(HrErrorCodes.ContractNotFixedTerm, "Only a fixed-term contract can be renewed.");
        }
    }

    /// <summary>
    /// Cierra el contrato su último día. Un registro de salario o de cargo que empieza después no puede quedar colgando.
    /// </summary>
    internal void Close(ContractStatus status, DateOnly lastDay, DateTimeOffset now)
    {
        EnsureCanClose(lastDay);
        Status = status;
        EndedOn = lastDay;
        UpdatedAt = now;
    }

    internal void EnsureCanClose(DateOnly lastDay)
    {
        EnsureActive();
        if (_salaries.Any(salary => salary.EffectiveDate > lastDay) || _assignments.Any(assignment => assignment.EffectiveDate > lastDay))
        {
            throw new HrDomainException(
                HrErrorCodes.RecordsAfterEnd,
                "A salary or assignment record starts after the last day of the contract.");
        }
    }

    internal void RecordTermination(TerminationReason reason, string? notes)
    {
        TerminationReason = reason;
        TerminationNotes = notes;
    }

    internal void AddSalary(
        SalaryRecordId id, SalaryEntry entry, SalaryFloor floorAtDate, DocumentRef? document, DateTimeOffset now)
    {
        EnsureActive();
        var last = _salaries.MaxBy(salary => salary.EffectiveDate)!;
        if (entry.EffectiveDate <= last.EffectiveDate)
        {
            throw new HrDomainException(
                HrErrorCodes.SalaryEffectiveDateNotAfterLast,
                "A new salary must take effect after the last one.");
        }

        var below = SalaryFloorPolicy.Check(ContractType, SalaryType, entry.Amount, floorAtDate, entry.AcknowledgeBelowMinimum);
        _salaries.Add(new SalaryRecord(id, Id, entry.EffectiveDate, entry.Amount, entry.Teleworker, below, document, now));
        UpdatedAt = now;
    }

    internal void RewriteSalary(
        SalaryRecordId id,
        decimal amount,
        bool teleworker,
        bool acknowledgeBelowMinimum,
        SalaryFloor floorAtRecordDate,
        DateTimeOffset now)
    {
        EnsureActive();
        var salary = FindSalary(id);
        var below = SalaryFloorPolicy.Check(ContractType, SalaryType, amount, floorAtRecordDate, acknowledgeBelowMinimum);
        salary.Rewrite(amount, teleworker, below, now);
        UpdatedAt = now;
    }

    internal void AddAssignment(AssignmentRecordId id, AssignmentEntry entry, DocumentRef? document, DateTimeOffset now)
    {
        EnsureActive();
        var previous = _assignments.MaxBy(assignment => assignment.EffectiveDate)!;
        if (entry.EffectiveDate <= previous.EffectiveDate)
        {
            throw new HrDomainException(
                HrErrorCodes.AssignmentEffectiveDateNotAfterLast,
                "A new assignment must take effect after the last one.");
        }

        EnsurePair(previous.ToEntry(), entry);

        _assignments.Add(new AssignmentRecord(
            id, Id, entry.EffectiveDate, entry.PositionId, entry.AreaId, entry.SiteId, entry.WorkplaceId, document, now));
        UpdatedAt = now;
    }

    /// <summary>
    /// Reescribe un registro de cargo y lugar (spec 2026-11-26, «Vecinos»). La fecha del primero es el inicio del contrato y no
    /// cambia; la de los demás queda estrictamente entre la del anterior y la del siguiente. Se revisan los pares
    /// anterior–editado y editado–siguiente.
    /// </summary>
    internal void RewriteAssignment(AssignmentRecordId id, AssignmentEntry entry, DateTimeOffset now)
    {
        EnsureActive();
        var (previous, record, next) = Neighbours(id);
        if (previous is null && entry.EffectiveDate != record.EffectiveDate)
        {
            throw new HrDomainException(
                HrErrorCodes.AssignmentFirstRecordDateFixed,
                "The first assignment record starts with the contract and its date cannot change.");
        }

        if ((previous is not null && entry.EffectiveDate <= previous.EffectiveDate)
            || (next is not null && entry.EffectiveDate >= next.EffectiveDate))
        {
            throw new HrDomainException(
                HrErrorCodes.AssignmentEffectiveDateOutOfOrder,
                "The new date must stay between the previous and the next assignment records.");
        }

        if (previous is not null)
        {
            EnsurePair(previous.ToEntry(), entry);
        }

        if (next is not null)
        {
            EnsurePair(entry, next.ToEntry());
        }

        record.Rewrite(entry);
        UpdatedAt = now;
    }

    /// <summary>
    /// Quita un registro de cargo y lugar y lo devuelve (quien llama lee su PDF). El primero no se quita; el anterior pasa a
    /// regir los días del quitado, así que se revisa el par anterior–siguiente.
    /// </summary>
    internal AssignmentRecord RemoveAssignment(AssignmentRecordId id, DateTimeOffset now)
    {
        EnsureActive();
        var (previous, record, next) = Neighbours(id);
        if (previous is null)
        {
            throw new HrDomainException(
                HrErrorCodes.AssignmentFirstRecordNotDeletable,
                "The first assignment record starts with the contract and cannot be deleted.");
        }

        if (next is not null)
        {
            EnsurePair(previous.ToEntry(), next.ToEntry());
        }

        _assignments.Remove(record);
        UpdatedAt = now;
        return record;
    }

    /// <summary>
    /// Consulta pura: si <see cref="RemoveAssignment"/> aceptaría quitar el registro por las reglas de vecinos. No mira si el
    /// contrato está activo (eso lo suma <c>canEdit</c>) ni muta nada.
    /// </summary>
    public bool CanRemoveAssignment(AssignmentRecordId id)
    {
        var ordered = _assignments.OrderBy(assignment => assignment.EffectiveDate).ToList();
        var index = ordered.FindIndex(assignment => assignment.Id == id);
        if (index <= 0)
        {
            return false;
        }

        return index == ordered.Count - 1 || PairViolation(ordered[index - 1].ToEntry(), ordered[index + 1].ToEntry()) is null;
    }

    private (AssignmentRecord? Previous, AssignmentRecord Record, AssignmentRecord? Next) Neighbours(AssignmentRecordId id)
    {
        var ordered = _assignments.OrderBy(assignment => assignment.EffectiveDate).ToList();
        var index = ordered.FindIndex(assignment => assignment.Id == id);
        if (index < 0)
        {
            throw new HrDomainException(HrErrorCodes.AssignmentNotFound, "The assignment record was not found.");
        }

        return (index > 0 ? ordered[index - 1] : null, ordered[index], index < ordered.Count - 1 ? ordered[index + 1] : null);
    }

    private static void EnsurePair(AssignmentEntry earlier, AssignmentEntry later)
    {
        if (PairViolation(earlier, later) is { } violation)
        {
            throw new HrDomainException(violation.Code, violation.Message);
        }
    }

    // La regla de un par de registros vecinos: el posterior cambia algo y, si cambia el lugar, empieza un día 1 (PILA,
    // FI-EMP-012). La única copia de la regla: la usan AddAssignment (contra el último registro), RewriteAssignment,
    // RemoveAssignment y CanRemoveAssignment.
    private static (string Code, string Message)? PairViolation(AssignmentEntry earlier, AssignmentEntry later)
    {
        var placeChanges = later.WorkplaceId != earlier.WorkplaceId || later.SiteId != earlier.SiteId;
        if (!placeChanges && later.PositionId == earlier.PositionId && later.AreaId == earlier.AreaId)
        {
            return (HrErrorCodes.AssignmentUnchanged, "Two neighbouring assignment records would be equal.");
        }

        if (placeChanges && later.EffectiveDate.Day != 1)
        {
            return (HrErrorCodes.SiteChangeNotFirstOfMonth, "The workplace can only change on the first day of a month.");
        }

        return null;
    }

    internal ContractRenewal Renew(ContractRenewalId id, DateOnly newEndDate, DocumentRef? document, DateTimeOffset now)
    {
        EnsureActive();
        EnsureFixedTerm();
        var currentEnd = EndDate
            ?? throw new InvalidOperationException("A fixed-term contract always has an end date; creation enforces it.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(newEndDate, currentEnd);
        if (newEndDate > FixedTermLimits.MaxEndDate(StartDate))
        {
            throw new HrDomainException(
                HrErrorCodes.FixedTermCapExceeded,
                "A fixed-term contract cannot last more than four years, renewals included.");
        }

        if (_renewals.Count(renewal => renewal.IsShort) >= FixedTermLimits.ShortRenewalsBeforeMinimumYear
            && FixedTermLimits.IsShort(currentEnd, newEndDate))
        {
            throw new HrDomainException(
                HrErrorCodes.RenewalMinimumOneYear,
                "After three renewals shorter than a year, the next one must last at least a year.");
        }

        var renewal = new ContractRenewal(id, Id, currentEnd, newEndDate, document, now);
        _renewals.Add(renewal);
        EndDate = newEndDate;
        UpdatedAt = now;
        return renewal;
    }

    internal void ConvertToIndefinite(DateOnly today, DateTimeOffset now)
    {
        EnsureActive();
        EnsureFixedTerm();
        Duration = ContractDuration.Indefinite;
        EndDate = null;
        ConvertedToIndefiniteOn = today;
        UpdatedAt = now;
    }

    internal void SetDocument(DocumentRef? document, DateTimeOffset now)
    {
        DocumentFileId = document?.FileId;
        DocumentFileName = document?.FileName;
        UpdatedAt = now;
    }

    internal void SetSalaryDocument(SalaryRecordId id, DocumentRef? document, DateTimeOffset now)
    {
        FindSalary(id).SetDocument(document, now);
        UpdatedAt = now;
    }

    internal void SetAssignmentDocument(AssignmentRecordId id, DocumentRef? document, DateTimeOffset now)
    {
        var assignment = _assignments.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new HrDomainException(HrErrorCodes.AssignmentNotFound, "The assignment record was not found.");
        assignment.SetDocument(document);
        UpdatedAt = now;
    }

    internal void SetRenewalDocument(ContractRenewalId id, DocumentRef? document, DateTimeOffset now)
    {
        var renewal = _renewals.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new HrDomainException(HrErrorCodes.RenewalNotFound, "The renewal was not found.");
        renewal.SetDocument(document);
        UpdatedAt = now;
    }

    private SalaryRecord FindSalary(SalaryRecordId id) =>
        _salaries.FirstOrDefault(candidate => candidate.Id == id)
        ?? throw new HrDomainException(HrErrorCodes.SalaryNotFound, "The salary record was not found.");
}
