namespace Modules.Payroll.Domain;

/// <summary>Una línea capturada para un empleado en un período, tal cual la dio quien capturó.</summary>
/// <param name="ConceptId">Un concepto del sistema (UUID fijo) o propio de la empresa.</param>
/// <param name="Hours">Sólo <c>Overtime</c>.</param>
/// <param name="Amount">Sólo <c>Income</c> y <c>Deduction</c>.</param>
/// <param name="SalaryConstitutive">Sólo <c>Income</c>: lo que quien capturó eligió.</param>
public sealed record PeriodInputDraft(Guid ConceptId, ConceptKind Kind, decimal? Hours, decimal? Amount, bool? SalaryConstitutive);

/// <summary>Una captura guardada. Pertenece a su <see cref="PayrollPeriod"/>: sólo el período la crea y la reemplaza.</summary>
public sealed class PeriodInput
{
    private PeriodInput()
    {
    }

    internal PeriodInput(PayrollPeriodId periodId, Guid employeeId, PeriodInputDraft draft, int position)
    {
        Id = Guid.CreateVersion7();
        PeriodId = periodId;
        EmployeeId = employeeId;
        ConceptId = draft.ConceptId;
        Kind = draft.Kind;
        Hours = draft.Hours;
        Amount = draft.Amount;
        SalaryConstitutive = draft.SalaryConstitutive;
        Position = position;
    }

    public Guid Id { get; private set; }

    public PayrollPeriodId PeriodId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public Guid ConceptId { get; private set; }

    public ConceptKind Kind { get; private set; }

    public decimal? Hours { get; private set; }

    public decimal? Amount { get; private set; }

    public bool? SalaryConstitutive { get; private set; }

    /// <summary>El orden dentro de su lista (horas, ingresos o deducciones): el <c>n</c> del código de línea <c>income:&lt;concepto&gt;:&lt;n&gt;</c>.</summary>
    public int Position { get; private set; }
}
