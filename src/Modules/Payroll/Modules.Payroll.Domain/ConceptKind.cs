namespace Modules.Payroll.Domain;

/// <summary>El tipo de un concepto de nómina: horas extras y recargos, ingreso o deducción.</summary>
public enum ConceptKind
{
    Overtime,
    Income,
    Deduction,
}
