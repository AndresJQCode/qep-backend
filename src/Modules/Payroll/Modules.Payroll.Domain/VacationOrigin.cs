namespace Modules.Payroll.Domain;

/// <summary>Who pays the vacation: a QEP quincena (<see cref="Qep"/>) or Siigo, typed in by hand (<see cref="External"/>).</summary>
public enum VacationOrigin
{
    Qep,
    External,
}
