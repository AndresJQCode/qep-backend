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

/// <summary>Los totales de las filas liquidadas, o los de una sola fila (<c>PayrollEmployeeSummaryDto</c> del contrato tiene esta misma forma): los calcula el backend, nunca el navegador.</summary>
public sealed record PayrollTotalsDto(
    decimal Salary,
    decimal VacationPay,
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
        summary.Salary, summary.VacationPay, summary.TransportAllowance, summary.Overtime, summary.OtherSalaryIncome, summary.NonSalaryIncome,
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
/// <param name="VacationDays">Los días de nómina de vacaciones de esta quincena, del resultado como <c>WorkedDays</c>: nulo hasta liquidar.</param>
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
    PayrollTotalsDto? Summary,
    int? VacationDays);

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

/// <param name="Vacations">Las que tocan la quincena (la pagan o tienen días en ella), para el diálogo de liquidar: los días son los de nómina en esta quincena.</param>
public sealed record PayrollPeriodDetailDto(
    PayrollPeriodDto Period,
    IReadOnlyList<PayrollEmployeeRowDto> Employees,
    IReadOnlyList<ExcludedEmployeeDto> Excluded,
    IReadOnlyList<BlockedEmployeeDto> Blocked,
    IReadOnlyList<PeriodEventDto> History,
    IReadOnlyList<PeriodVacationDto> Vacations);

/// <param name="CurrentPeriodId">El período que contiene el día del tenant; nulo si no existe. La pantalla aterriza en él, no en uno abierto por adelantado.</param>
/// <param name="NextPeriodStartsOn">Inicio de la quincena siguiente a la actual mientras no exista; nulo si ya existe.</param>
/// <param name="FirstPeriodStartsOn">Sólo sin períodos y con hoy en una segunda quincena (R-13): el día 1 del mes siguiente.</param>
public sealed record PayrollPeriodListDto(
    PayrollCompanyDto Company,
    IReadOnlyList<PayrollPeriodDto> Items,
    DateOnly? FirstPeriodStartsOn,
    DateOnly? NextPeriodStartsOn,
    Guid? CurrentPeriodId);

// --- Capturas, recurrentes y rastro (Tarea 10, pre-flight M-14) ---

/// <param name="Hours">Sólo <c>Overtime</c>.</param>
/// <param name="Amount">Sólo <c>Income</c> y <c>Deduction</c>.</param>
/// <param name="SalaryConstitutive">Sólo <c>Income</c>.</param>
public sealed record InputLineDto(
    Guid ConceptId, string ConceptName, string Kind, decimal? Hours, decimal? Amount, bool? SalaryConstitutive);

public sealed record EmployeeInputsDto(
    IReadOnlyList<InputLineDto> Overtime, IReadOnlyList<InputLineDto> Incomes, IReadOnlyList<InputLineDto> Deductions)
{
    public static EmployeeInputsDto Empty { get; } = new([], [], []);
}

public sealed record RecurringConceptRefDto(Guid Id, string Name, string Kind);

/// <param name="SalaryConstitutive">Nulo en una deducción.</param>
/// <param name="Accumulated">Lo aplicado en las últimas fotos de los períodos anteriores que no son Borrador (R-12).</param>
/// <param name="Remaining">Lo que queda del tope; nulo sin tope.</param>
public sealed record RecurringConceptDto(
    Guid Id,
    Guid EmployeeId,
    RecurringConceptRefDto Concept,
    string Mode,
    decimal Amount,
    bool? SalaryConstitutive,
    decimal? Cap,
    decimal Accumulated,
    decimal? Remaining,
    string Status,
    long Version);

/// <param name="Value">Un número o un texto, tal como lo leyó la regla.</param>
public sealed record TraceInputDto(string Name, object Value);

public sealed record LegalValueUseDto(string Key, string Label, decimal Value, DateOnly EffectiveFrom, string Basis);

public sealed record PayrollLineTraceDto(
    string Formula, IReadOnlyList<TraceInputDto> Inputs, IReadOnlyList<LegalValueUseDto> LegalValues, string? Note);

public sealed record PayrollLineDto(string Code, string Group, string Label, decimal Amount, PayrollLineTraceDto Trace);

/// <summary>El resultado guardado de la última liquidación: líneas con su rastro, resumen y la foto legal completa del período.</summary>
public sealed record EmployeeResultDto(
    int WorkedDays,
    string EngineVersion,
    IReadOnlyList<PayrollLineDto> Lines,
    PayrollTotalsDto Summary,
    IReadOnlyList<LegalValueUseDto> LegalValues);

/// <param name="Result">Nulo hasta liquidar y para un excluido.</param>
/// <param name="Vacations">Las vacaciones del empleado que tocan la quincena: la pagan o tienen días en ella.</param>
public sealed record PayrollEmployeeDetailDto(
    PayrollPeriodDto Period,
    PayrollEmployeeRowDto Employee,
    EmployeeInputsDto Inputs,
    IReadOnlyList<RecurringConceptDto> Recurring,
    EmployeeResultDto? Result,
    IReadOnlyList<VacationDto> Vacations);
