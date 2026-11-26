namespace Modules.Payroll.Application;

// Capturas y recurrentes (contrato HTTP, «Requests»). Todo es anulable: el validador devuelve el campo, no un 400 de binding.

/// <summary>Una captura de horas de <c>PUT {p}/employees/{id}/inputs</c>.</summary>
public sealed record OvertimeInputRequest(Guid? ConceptId, decimal? Hours);

public sealed record IncomeInputRequest(Guid? ConceptId, decimal? Amount, bool? SalaryConstitutive);

public sealed record DeductionInputRequest(Guid? ConceptId, decimal? Amount);

/// <summary>Reemplaza las tres listas del empleado en la quincena (≤ 20 horas, ≤ 50 ingresos, ≤ 50 deducciones).</summary>
public sealed record EmployeeInputsRequest(
    IReadOnlyList<OvertimeInputRequest>? Overtime,
    IReadOnlyList<IncomeInputRequest>? Incomes,
    IReadOnlyList<DeductionInputRequest>? Deductions);

/// <summary>
/// <c>POST|PUT {c}/employees/{id}/recurring-concepts</c>. <c>SalaryConstitutive</c> es requerido con un ingreso y nulo con una
/// deducción: eso depende del tipo del concepto, así que lo valida el caso de uso. <c>Cap</c> ≥ <c>Amount</c> si viene.
/// </summary>
public sealed record RecurringConceptRequest(Guid? ConceptId, string? Mode, decimal? Amount, bool? SalaryConstitutive, decimal? Cap);
