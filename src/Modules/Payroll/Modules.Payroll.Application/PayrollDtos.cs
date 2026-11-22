namespace Modules.Payroll.Application;

// DTO de respuesta (contrato HTTP, «DTOs»). Los enums viajan como el nombre exacto del miembro, en texto; los opcionales van
// siempre presentes como null. Los del período llegan con la Tarea 9, los de capturas y recurrentes con la 10.

public sealed record PayrollCompanyDto(Guid Id, string Name, string TaxId, bool IsActive);

/// <param name="System">Los del sistema no son filas: su id es fijo y no se editan ni se archivan.</param>
/// <param name="SystemKey">El nombre del <c>SystemConceptKey</c>; nulo en los propios.</param>
/// <param name="DefaultSalaryConstitutive">Sólo <c>Income</c>; nulo en los demás (y en un ingreso sin sugerencia).</param>
/// <param name="Multiplier">Sólo un <c>Overtime</c> propio; los del sistema salen de la tabla legal (nulo).</param>
/// <param name="Version">Nulo en los del sistema: no se editan.</param>
public sealed record PayrollConceptDto(
    Guid Id,
    string Kind,
    string Name,
    bool System,
    string? SystemKey,
    bool? DefaultSalaryConstitutive,
    decimal? Multiplier,
    bool Archived,
    long? Version);

/// <param name="Version">0 = nunca guardada: son los valores por defecto (R-17).</param>
/// <param name="Concepts">El catálogo: los del sistema primero en el orden de la tabla, luego los propios por tipo y nombre.</param>
public sealed record PayrollSettingsDto(
    PayrollCompanyDto Company,
    string TransportFrequency,
    string TransportCapBase,
    string OvertimeBase,
    bool Exonerated,
    bool DualControl,
    long Version,
    IReadOnlyList<PayrollConceptDto> Concepts);

// --- Período (Tarea 9) ---

/// <summary>Quien escribió: el nombre de su membresía al momento de escribir ("Sin nombre" si no tenía).</summary>
public sealed record ActorDto(Guid SubjectId, string DisplayName);

/// <summary>Los totales de las filas liquidadas: los calcula el backend, nunca el navegador.</summary>
public sealed record PayrollTotalsDto(
    decimal Salary,
    decimal TransportAllowance,
    decimal Overtime,
    decimal OtherSalaryIncome,
    decimal NonSalaryIncome,
    decimal TotalEarned,
    decimal HealthEmployee,
    decimal PensionEmployee,
    decimal SolidarityFund,
    decimal OtherDeductions,
    decimal TotalDeductions,
    decimal Net,
    decimal HealthEmployer,
    decimal PensionEmployer,
    decimal Arl,
    decimal CompensationFund,
    decimal Icbf,
    decimal Sena,
    decimal Severance,
    decimal SeveranceInterest,
    decimal Premium,
    decimal Vacation,
    decimal EmployerCost)
{
    public static PayrollTotalsDto From(Modules.Payroll.Domain.Engine.PayrollSummary summary) => new(
        summary.Salary, summary.TransportAllowance, summary.Overtime, summary.OtherSalaryIncome, summary.NonSalaryIncome,
        summary.TotalEarned, summary.HealthEmployee, summary.PensionEmployee, summary.SolidarityFund, summary.OtherDeductions,
        summary.TotalDeductions, summary.Net, summary.HealthEmployer, summary.PensionEmployer, summary.Arl,
        summary.CompensationFund, summary.Icbf, summary.Sena, summary.Severance, summary.SeveranceInterest, summary.Premium,
        summary.Vacation, summary.EmployerCost);
}

/// <summary>Misma forma que <see cref="PayrollTotalsDto"/>, para una fila.</summary>
public sealed record PayrollEmployeeSummaryDto(
    decimal Salary,
    decimal TransportAllowance,
    decimal Overtime,
    decimal OtherSalaryIncome,
    decimal NonSalaryIncome,
    decimal TotalEarned,
    decimal HealthEmployee,
    decimal PensionEmployee,
    decimal SolidarityFund,
    decimal OtherDeductions,
    decimal TotalDeductions,
    decimal Net,
    decimal HealthEmployer,
    decimal PensionEmployer,
    decimal Arl,
    decimal CompensationFund,
    decimal Icbf,
    decimal Sena,
    decimal Severance,
    decimal SeveranceInterest,
    decimal Premium,
    decimal Vacation,
    decimal EmployerCost)
{
    public static PayrollEmployeeSummaryDto From(Modules.Payroll.Domain.Engine.PayrollSummary summary) => new(
        summary.Salary, summary.TransportAllowance, summary.Overtime, summary.OtherSalaryIncome, summary.NonSalaryIncome,
        summary.TotalEarned, summary.HealthEmployee, summary.PensionEmployee, summary.SolidarityFund, summary.OtherDeductions,
        summary.TotalDeductions, summary.Net, summary.HealthEmployer, summary.PensionEmployer, summary.Arl,
        summary.CompensationFund, summary.Icbf, summary.Sena, summary.Severance, summary.SeveranceInterest, summary.Premium,
        summary.Vacation, summary.EmployerCost);
}

/// <summary>Qué ofrecer: ya cruza el estado, los permisos de quien llama y el doble control.</summary>
public sealed record PayrollPeriodCanDto(bool Capture, bool Liquidate, bool Approve, bool Close, bool Reopen, bool Export);

/// <param name="Outdated">Guardado: una captura o la primera quincena cambió después de liquidar.</param>
/// <param name="SourceChanged">Calculado al leer un Liquidado: Hr o la configuración cambiaron (R-16).</param>
/// <param name="EmployeeCount">Liquidables, con o sin bloqueo; no cuenta los excluidos.</param>
/// <param name="Totals">Nulo hasta liquidar.</param>
/// <param name="ApproveBlockedBy">Subconjunto ordenado de status, outdated, source_changed, negative_net, first_half_not_approved, dual_control.</param>
public sealed record PayrollPeriodDto(
    Guid Id,
    PayrollCompanyDto Company,
    DateOnly StartDate,
    DateOnly EndDate,
    string Half,
    string Status,
    bool Outdated,
    bool SourceChanged,
    long Version,
    DateOnly? LegalValuesDate,
    string? EngineVersion,
    DateTimeOffset? LastLiquidatedAt,
    ActorDto? LastLiquidatedBy,
    DateTimeOffset? ApprovedAt,
    ActorDto? ApprovedBy,
    DateTimeOffset? ClosedAt,
    ActorDto? ClosedBy,
    int EmployeeCount,
    PayrollTotalsDto? Totals,
    PayrollPeriodCanDto Can,
    IReadOnlyList<string> ApproveBlockedBy);

/// <param name="WorkedDays">Nulo hasta liquidar.</param>
/// <param name="HasInputs">Tiene horas, ingresos o deducciones capturados en la quincena.</param>
/// <param name="FinalSettlementPending">El contrato termina dentro de la quincena.</param>
/// <param name="Summary">Nulo si no está liquidado (o entró a la quincena después de liquidar).</param>
public sealed record PayrollEmployeeRowDto(
    Guid EmployeeId,
    string DocumentType,
    string DocumentNumber,
    string FullName,
    string? Position,
    string? Workplace,
    int? WorkedDays,
    bool HasInputs,
    bool FinalSettlementPending,
    bool NegativeNet,
    PayrollEmployeeSummaryDto? Summary);

public sealed record ExcludedEmployeeDto(Guid EmployeeId, string DocumentType, string DocumentNumber, string FullName, string Reason);

/// <summary>El empleado ya está en <c>employees</c> (I-9); acá sólo los motivos.</summary>
public sealed record BlockedEmployeeDto(Guid EmployeeId, IReadOnlyList<string> Reasons);

public sealed record PeriodEventDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    ActorDto Actor,
    string Action,
    string? FromStatus,
    string? ToStatus,
    string? Reason,
    bool? AbsencesConfirmed);

public sealed record PayrollPeriodDetailDto(
    PayrollPeriodDto Period,
    IReadOnlyList<PayrollEmployeeRowDto> Employees,
    IReadOnlyList<ExcludedEmployeeDto> Excluded,
    IReadOnlyList<BlockedEmployeeDto> Blocked,
    IReadOnlyList<PeriodEventDto> History);

/// <param name="FirstPeriodStartsOn">Sólo sin períodos y con hoy en una segunda quincena (R-13): el día 1 del mes siguiente.</param>
public sealed record PayrollPeriodListDto(
    PayrollCompanyDto Company,
    IReadOnlyList<PayrollPeriodDto> Items,
    DateOnly? FirstPeriodStartsOn);
