namespace Modules.Payroll.Domain.Engine;

/// <summary>El resultado del motor para un empleado en una quincena. <paramref name="Month"/> es lo que la segunda quincena recibe como <c>Prior</c>.</summary>
public sealed record EmployeeLiquidation(
    Guid EmployeeId,
    int WorkedDays,
    bool FinalSettlementPending,
    bool NegativeNet,
    IReadOnlyList<PayrollLine> Lines,
    PayrollSummary Summary,
    MonthAccumulators Month,
    string EngineVersion,
    int VacationDays,
    IReadOnlyList<VacationPhoto> VacationPhotos);
