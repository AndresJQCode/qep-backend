namespace Modules.Payroll.Domain;

/// <summary>El grupo de una línea del resultado. El orden de salida NO es el de este enum: lo fija <c>PayrollEngine</c> (I-11).</summary>
public enum PayrollLineGroup
{
    Earning,
    Deduction,
    EmployerContribution,
    Provision,
    Info,
}
