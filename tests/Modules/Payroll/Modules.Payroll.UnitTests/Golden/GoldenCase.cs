using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Golden;

/// <summary>
/// Un caso de oro: la entrada completa del motor (sin la foto legal, que viene de <see cref="Legal"/>) y el resultado esperado línea
/// por línea. Lo leen las pruebas sintéticas y, luego, las del caso real de Siigo (formato compartido, <see cref="Source"/>).
/// </summary>
public sealed record GoldenCase(
    string Name,
    string Source,
    string Description,
    string Legal,
    IReadOnlyList<string> Covers,
    GoldenInput Input,
    GoldenExpected Expected,
    IReadOnlyList<string> Notes);

/// <summary>El <see cref="EngineInput"/> serializado, sin <c>Legal</c>. Las listas que faltan en el archivo son vacías y el <c>Prior</c> ausente es el mes en cero.</summary>
public sealed record GoldenInput(
    Guid EmployeeId,
    PeriodInfo Period,
    EngineSettings Settings,
    DateOnly ContractStart,
    DateOnly? ContractEndedOn,
    IReadOnlyList<SalarySegmentSource> Salaries,
    WorkplaceInfo Workplace,
    IReadOnlyList<OvertimeInput>? Overtime,
    IReadOnlyList<IncomeInput>? Incomes,
    IReadOnlyList<DeductionInput>? Deductions,
    IReadOnlyList<RecurringInput>? Recurring,
    MonthAccumulators? Prior,
    IReadOnlyList<VacationInput>? Vacations = null)
{
    public EngineInput ToEngineInput(LegalValueSet legal) =>
        new(
            EmployeeId,
            Period,
            Settings,
            legal,
            ContractStart,
            ContractEndedOn,
            Salaries,
            Workplace,
            Overtime ?? [],
            Incomes ?? [],
            Deductions ?? [],
            Recurring ?? [],
            Prior ?? MonthAccumulators.Empty,
            Vacations ?? []);
}

public sealed record GoldenExpected(
    int WorkedDays,
    bool FinalSettlementPending,
    bool NegativeNet,
    IReadOnlyList<GoldenLine> Lines,
    decimal Net,
    decimal EmployerCost,
    int? VacationDays = null);

public sealed record GoldenLine(string Code, decimal Amount);
