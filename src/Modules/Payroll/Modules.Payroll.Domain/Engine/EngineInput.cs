namespace Modules.Payroll.Domain.Engine;

public sealed record PeriodInfo(DateOnly Start, DateOnly End, PayrollHalf Half);

public sealed record EngineSettings(
    TransportFrequency TransportFrequency,
    TransportCapBase TransportCapBase,
    OvertimeBase OvertimeBase,
    bool Exonerated);

/// <summary>Un registro de salario del contrato (todos, en orden): rige desde su fecha hasta el día antes del siguiente.</summary>
public sealed record SalarySegmentSource(DateOnly EffectiveDate, decimal MonthlyAmount, bool Teleworker);

public sealed record WorkplaceInfo(string Name, ArlClass RiskClass, bool HighRiskPension);

/// <summary>
/// Una captura de horas. Exactamente uno de <paramref name="SystemKey"/> y <paramref name="CustomMultiplier"/> viene: el factor no
/// viaja, lo deriva <see cref="OvertimeFactors"/> de los valores legales (R-2).
/// </summary>
public sealed record OvertimeInput(
    Guid ConceptId,
    string ConceptName,
    decimal Hours,
    SystemConceptKey? SystemKey,
    decimal? CustomMultiplier);

public sealed record IncomeInput(Guid ConceptId, string ConceptName, decimal Amount, bool SalaryConstitutive);

public sealed record DeductionInput(Guid ConceptId, string ConceptName, decimal Amount);

/// <summary>Un concepto recurrente vigente; <paramref name="RemainingCap"/> es lo que le queda de su tope (nulo si no tiene).</summary>
public sealed record RecurringInput(
    Guid RecurringId,
    string ConceptName,
    ConceptKind Kind,
    RecurringMode Mode,
    decimal Amount,
    bool SalaryConstitutive,
    decimal? RemainingCap);

/// <summary>Todo lo que el motor lee de una quincena de un empleado. Sin fecha ni reloj: el período y la foto legal vienen adentro.</summary>
public sealed record EngineInput(
    Guid EmployeeId,
    PeriodInfo Period,
    EngineSettings Settings,
    LegalValueSet Legal,
    DateOnly ContractStart,
    DateOnly? ContractEndedOn,
    IReadOnlyList<SalarySegmentSource> Salaries,
    WorkplaceInfo Workplace,
    IReadOnlyList<OvertimeInput> Overtime,
    IReadOnlyList<IncomeInput> Incomes,
    IReadOnlyList<DeductionInput> Deductions,
    IReadOnlyList<RecurringInput> Recurring,
    MonthAccumulators Prior,
    IReadOnlyList<VacationInput> Vacations);
