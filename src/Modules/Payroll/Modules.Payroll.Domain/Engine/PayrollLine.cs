namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Una línea del resultado. <paramref name="SalaryConstitutive"/> sólo viene en los ingresos capturados y recurrentes (pre-flight
/// I-2): el resumen separa ingresos salariales de no salariales a partir de ella.
/// </summary>
public sealed record PayrollLine(
    string Code,
    PayrollLineGroup Group,
    string Label,
    decimal Amount,
    bool? SalaryConstitutive,
    LineTrace Trace);
