namespace Modules.Hr.Domain;

/// <summary>
/// El empleado y todo lo que cuelga de él (contratos y sus historiales): una sola versión, <see cref="Version"/>, que sube con
/// cualquier escritura. Los métodos que validan salario reciben el <see cref="SalaryFloor"/> ya resuelto a la fecha que toca.
/// </summary>
public sealed class Employee
{
    private readonly List<EmploymentContract> _contracts = [];

    private Employee()
    {
    }

    public EmployeeId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    public HrDocumentType DocumentType { get; private set; }

    public string DocumentNumber { get; private set; } = string.Empty;

    public string Country { get; private set; } = string.Empty;

    public DateOnly? BirthDate { get; private set; }

    public string? Phone { get; private set; }

    public string? PersonalEmail { get; private set; }

    public string? WorkEmail { get; private set; }

    public string? Address { get; private set; }

    public Guid? CityId { get; private set; }

    public Guid? MemberId { get; private set; }

    public string? HealthProvider { get; private set; }

    public string? PensionFund { get; private set; }

    public string? SeveranceFund { get; private set; }

    public string? OccupationalRiskProvider { get; private set; }


    public ContributorSubtype ContributorSubtype { get; private set; }

    public bool ForeignPensionExempt { get; private set; }

    public IReadOnlyList<DayOfWeek> RestDays { get; private set; } = [];

    // Los cuatro campos del pago son columnas propias; `Payment` los junta (nulo si nunca se cargó).
    public PaymentMethod? PaymentMethod { get; private set; }

    public string? BankName { get; private set; }

    public BankAccountType? BankAccountType { get; private set; }

    public string? BankAccountNumber { get; private set; }

    public PaymentDetails? Payment =>
        PaymentMethod is { } method ? new PaymentDetails(method, BankName, BankAccountType, BankAccountNumber) : null;

    public EmployeeStatus Status { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<EmploymentContract> Contracts => _contracts;

    public EmploymentContract? ActiveContract => _contracts.SingleOrDefault(contract => contract.Status == ContractStatus.Active);

    /// <summary>Spec, «Ciclo de vida»: sólo un alta recién hecha, sin ningún movimiento posterior, se puede eliminar.</summary>
    public bool CanDelete =>
        _contracts is [{ Status: ContractStatus.Active, ConvertedToIndefiniteOn: null, Renewals.Count: 0, Salaries.Count: 1, Assignments.Count: 1 }];

    public static Employee Hire(
        EmployeeId id,
        Guid tenantId,
        EmployeeProfile profile,
        PaymentDetails? payment,
        NewContract contract,
        SalaryFloor floorAtStart,
        DateTimeOffset now)
    {
        var employee = new Employee { Id = id, TenantId = tenantId, CreatedAt = now };
        employee.ApplyProfile(profile);
        employee.ApplyPayment(payment);
        employee.AddContract(contract, null, floorAtStart, now);
        employee.Touch(now);
        employee.Version = 1;
        return employee;
    }

    public void UpdateProfile(EmployeeProfile profile, DateTimeOffset now)
    {
        ApplyProfile(profile);
        Touch(now);
    }

    public void SetPayment(PaymentDetails? payment, DateTimeOffset now)
    {
        ApplyPayment(payment);
        Touch(now);
    }

    public void AddSalary(
        EmploymentContractId contractId,
        SalaryRecordId id,
        SalaryEntry entry,
        SalaryFloor floorAtDate,
        DocumentRef? document,
        DateTimeOffset now)
    {
        FindContract(contractId).AddSalary(id, entry, floorAtDate, document, now);
        Touch(now);
    }

    public void RewriteSalary(
        EmploymentContractId contractId,
        SalaryRecordId id,
        decimal amount,
        bool teleworker,
        bool acknowledgeBelowMinimum,
        bool confirmRewrite,
        SalaryFloor floorAtRecordDate,
        DateTimeOffset now)
    {
        // Antes que todo lo demás: reescribir un registro histórico sin confirmar nunca llega a evaluar el resto.
        if (!confirmRewrite)
        {
            throw new HrDomainException(
                HrErrorCodes.SalaryRewriteUnconfirmed,
                "Rewriting a salary record needs an explicit confirmation.");
        }

        FindContract(contractId).RewriteSalary(id, amount, teleworker, acknowledgeBelowMinimum, floorAtRecordDate, now);
        Touch(now);
    }

    public void AddAssignment(
        EmploymentContractId contractId,
        AssignmentRecordId id,
        AssignmentEntry entry,
        DocumentRef? document,
        DateTimeOffset now)
    {
        FindContract(contractId).AddAssignment(id, entry, document, now);
        Touch(now);
    }

    public void RewriteAssignment(
        EmploymentContractId contractId,
        AssignmentRecordId id,
        AssignmentEntry entry,
        bool confirmRewrite,
        DateTimeOffset now)
    {
        // Como RewriteSalary: reescribir un registro histórico sin confirmar nunca llega a evaluar el resto.
        if (!confirmRewrite)
        {
            throw new HrDomainException(
                HrErrorCodes.AssignmentRewriteUnconfirmed,
                "Rewriting an assignment record needs an explicit confirmation.");
        }

        FindContract(contractId).RewriteAssignment(id, entry, now);
        Touch(now);
    }

    public AssignmentRecord RemoveAssignment(EmploymentContractId contractId, AssignmentRecordId id, DateTimeOffset now)
    {
        var removed = FindContract(contractId).RemoveAssignment(id, now);
        Touch(now);
        return removed;
    }

    public ContractRenewal Renew(
        EmploymentContractId contractId,
        ContractRenewalId id,
        DateOnly newEndDate,
        DocumentRef? document,
        DateTimeOffset now)
    {
        var renewal = FindContract(contractId).Renew(id, newEndDate, document, now);
        Touch(now);
        return renewal;
    }

    public void ConvertToIndefinite(EmploymentContractId contractId, DateOnly today, DateTimeOffset now)
    {
        FindContract(contractId).ConvertToIndefinite(today, now);
        Touch(now);
    }

    public void ChangeContractType(
        EmploymentContractId contractId, NewContract next, SalaryFloor floorAtStart, DateTimeOffset now)
    {
        var current = FindContract(contractId);
        current.EnsureActive();

        // A type conversion keeps the employer; changing company is terminate + reincorporate.
        if (next.Terms.CompanyId != current.CompanyId)
        {
            throw new HrDomainException(
                HrErrorCodes.CompanyChangeNotAllowed,
                "A contract type change keeps the employer; to change company, terminate and reincorporate.");
        }

        if (next.Terms.StartDate <= current.StartDate)
        {
            throw new HrDomainException(
                HrErrorCodes.StartNotAfterPrevious,
                "The new contract must start after the one it replaces.");
        }

        // Everything that can fail runs before anything is mutated, so a rejected conversion leaves the aggregate intact.
        var lastDay = next.Terms.StartDate.AddDays(-1);
        current.EnsureCanClose(lastDay);
        var below = ValidateNewContract(next, floorAtStart);

        current.Close(ContractStatus.Converted, lastDay, now);
        _contracts.Add(EmploymentContract.Create(Id, next, current.Id, below, now));
        Touch(now);
    }

    public void Terminate(
        EmploymentContractId contractId, TerminationReason reason, DateOnly date, string? notes, DateTimeOffset now)
    {
        var contract = FindContract(contractId);
        ArgumentOutOfRangeException.ThrowIfLessThan(date, contract.StartDate);
        contract.Close(ContractStatus.Terminated, date, now);
        contract.RecordTermination(reason, notes);
        Touch(now);
    }

    public void Deactivate(DateOnly date, DateTimeOffset now)
    {
        var contract = ActiveContract
            ?? throw new HrDomainException(HrErrorCodes.EmployeeNotActive, "The employee has no active contract.");
        ArgumentOutOfRangeException.ThrowIfLessThan(date, contract.StartDate);
        contract.Close(ContractStatus.Closed, date, now);
        Touch(now);
    }

    /// <summary>La empresa puede ser otra: reincorporar es la única forma de cambiar de empresa.</summary>
    public void Reincorporate(NewContract next, SalaryFloor floorAtStart, DateTimeOffset now)
    {
        if (ActiveContract is not null)
        {
            throw new HrDomainException(HrErrorCodes.EmployeeAlreadyActive, "The employee already has an active contract.");
        }

        var lastEnd = _contracts.Max(contract => contract.EndedOn);
        if (lastEnd is not null && next.Terms.StartDate <= lastEnd)
        {
            throw new HrDomainException(
                HrErrorCodes.StartNotAfterPrevious,
                "The new contract must start after the previous one ended.");
        }

        AddContract(next, null, floorAtStart, now);
        Touch(now);
    }

    public void SetContractDocument(EmploymentContractId contractId, DocumentRef? document, DateTimeOffset now)
    {
        FindContract(contractId).SetDocument(document, now);
        Touch(now);
    }

    public void SetSalaryDocument(
        EmploymentContractId contractId, SalaryRecordId id, DocumentRef? document, DateTimeOffset now)
    {
        FindContract(contractId).SetSalaryDocument(id, document, now);
        Touch(now);
    }

    public void SetAssignmentDocument(
        EmploymentContractId contractId, AssignmentRecordId id, DocumentRef? document, DateTimeOffset now)
    {
        FindContract(contractId).SetAssignmentDocument(id, document, now);
        Touch(now);
    }

    public void SetRenewalDocument(
        EmploymentContractId contractId, ContractRenewalId id, DocumentRef? document, DateTimeOffset now)
    {
        FindContract(contractId).SetRenewalDocument(id, document, now);
        Touch(now);
    }

    public void EnsureDeletable()
    {
        if (!CanDelete)
        {
            throw new HrDomainException(
                HrErrorCodes.EmployeeNotDeletable,
                "Only an employee without any movement after the hire can be deleted.");
        }
    }

    private void AddContract(NewContract next, EmploymentContractId? previousContractId, SalaryFloor floorAtStart, DateTimeOffset now)
    {
        var below = ValidateNewContract(next, floorAtStart);
        _contracts.Add(EmploymentContract.Create(Id, next, previousContractId, below, now));
    }

    // Todo contrato nuevo pasa por acá: el tope de cuatro años del término fijo y el piso del salario a la fecha de inicio.
    private static bool ValidateNewContract(NewContract next, SalaryFloor floorAtStart)
    {
        var terms = next.Terms;

        // Application validates this with a field error; the aggregate does not rely on it, because `Renew` and the aviso de
        // vencimiento read `EndDate` of a fixed-term contract without a null check.
        if (terms is { Duration: ContractDuration.FixedTerm or ContractDuration.WorkOrLabor, EndDate: null })
        {
            throw new ArgumentException("A fixed-term or work-or-labor contract needs an end date.", nameof(next));
        }

        if (terms is { Duration: ContractDuration.FixedTerm, EndDate: { } end } && end > FixedTermLimits.MaxEndDate(terms.StartDate))
        {
            throw new HrDomainException(
                HrErrorCodes.FixedTermCapExceeded,
                "A fixed-term contract cannot last more than four years.");
        }

        return SalaryFloorPolicy.Check(
            terms.ContractType, terms.SalaryType, next.Amount, floorAtStart, next.AcknowledgeBelowMinimum);
    }

    private EmploymentContract FindContract(EmploymentContractId id) =>
        _contracts.FirstOrDefault(contract => contract.Id == id)
        ?? throw new HrDomainException(HrErrorCodes.ContractNotFound, "The contract was not found.");

    private void ApplyProfile(EmployeeProfile profile)
    {
        FirstName = profile.FirstName;
        LastName = profile.LastName;
        DocumentType = profile.DocumentType;
        DocumentNumber = profile.DocumentNumber;
        Country = profile.Country;
        BirthDate = profile.BirthDate;
        Phone = profile.Phone;
        PersonalEmail = profile.PersonalEmail;
        WorkEmail = profile.WorkEmail;
        Address = profile.Address;
        CityId = profile.CityId;
        MemberId = profile.MemberId;
        HealthProvider = profile.HealthProvider;
        PensionFund = profile.PensionFund;
        SeveranceFund = profile.SeveranceFund;
        OccupationalRiskProvider = profile.OccupationalRiskProvider;
        ContributorSubtype = profile.ContributorSubtype;
        ForeignPensionExempt = profile.ForeignPensionExempt;
        RestDays = profile.RestDays.ToArray();
    }

    private void ApplyPayment(PaymentDetails? payment)
    {
        PaymentMethod = payment?.Method;
        BankName = payment?.BankName;
        BankAccountType = payment?.BankAccountType;
        BankAccountNumber = payment?.BankAccountNumber;
    }

    // Toda mutación termina acá: sube la versión (token de concurrencia) y recalcula el estado, que se guarda para filtrar la lista.
    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_contracts.Any(contract => contract.Status == ContractStatus.Active))
        {
            Status = EmployeeStatus.Active;
            return;
        }

        var latest = _contracts.MaxBy(contract => contract.StartDate);
        Status = latest?.Status switch
        {
            ContractStatus.Terminated => EmployeeStatus.Retired,
            ContractStatus.Closed => EmployeeStatus.Deactivated,
            _ => Status,
        };
    }
}
