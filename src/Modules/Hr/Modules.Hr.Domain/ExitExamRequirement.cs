namespace Modules.Hr.Domain;

/// <summary>
/// The pending exit exam of a terminated contract (spec 2026-11-23 §5.3): one per contract. It is born with the termination and
/// <see cref="MedicalExamRules.Recalculate"/> decides after every exam write whether an exit exam closes it.
/// </summary>
public sealed class ExitExamRequirement
{
    private ExitExamRequirement()
    {
    }

    public ExitExamRequirementId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public EmployeeId EmployeeId { get; private set; }

    public EmploymentContractId ContractId { get; private set; }

    public DateOnly TerminatedOn { get; private set; }

    public DateOnly DueDate { get; private set; }

    public ExitExamStatus Status { get; private set; }

    public MedicalExamId? ExamId { get; private set; }

    public DateOnly? NotifiedOn { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ExitExamRequirement Create(
        ExitExamRequirementId id,
        Guid tenantId,
        EmployeeId employeeId,
        EmploymentContractId contractId,
        DateOnly terminatedOn,
        DateOnly dueDate,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            EmployeeId = employeeId,
            ContractId = contractId,
            TerminatedOn = terminatedOn,
            DueDate = dueDate,
            Status = ExitExamStatus.Pending,
            CreatedAt = now,
        };

    /// <summary>The notice the employer owes the worker. Only a <c>Pending</c> requirement takes it; it is kept for good.</summary>
    public void MarkNotAttended(DateOnly notifiedOn, string? notes, DateOnly today)
    {
        if (Status != ExitExamStatus.Pending)
        {
            throw new HrDomainException(HrErrorCodes.MedicalExamNoPendingExit, "There is no pending exit exam.");
        }

        if (notifiedOn > today)
        {
            throw new HrDomainException(
                HrErrorCodes.MedicalExamFutureDate,
                "The notice date cannot be in the future.",
                new Dictionary<string, string[]> { ["notifiedOn"] = ["La fecha del aviso no puede ser futura."] });
        }

        Status = ExitExamStatus.NotAttended;
        NotifiedOn = notifiedOn;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    /// <summary>An exit exam closes it; a previous notice is kept, so reopening later returns to <c>NotAttended</c>.</summary>
    internal void Close(MedicalExamId examId)
    {
        Status = ExitExamStatus.Done;
        ExamId = examId;
    }

    internal void Reopen()
    {
        Status = NotifiedOn is null ? ExitExamStatus.Pending : ExitExamStatus.NotAttended;
        ExamId = null;
    }
}
