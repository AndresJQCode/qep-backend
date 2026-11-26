namespace Modules.Payroll.Domain;

/// <summary>Qué le falta a un empleado liquidable para poder liquidarlo: bloquea la liquidación de todo el período (R-15).</summary>
public enum BlockerReason
{
    MissingSalary,
    MissingWorkplace,
    MissingRiskClass,
    VacationOutsideContract,
}
