namespace Modules.Payroll.Domain;

/// <summary>Quien actúa sobre un período: el usuario y el nombre de su membresía en el momento de escribir ("Sin nombre" si no tenía).</summary>
public sealed record PayrollActor(Guid SubjectId, string DisplayName);
