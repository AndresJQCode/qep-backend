namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Los únicos redondeos del motor (R-6). Los valores intermedios (valor hora, <c>SalaryRaw</c>, bases) no se redondean: sólo la
/// línea que se paga.
/// </summary>
public static class PayrollRounding
{
    /// <summary>Al peso, mitad hacia arriba.</summary>
    public static decimal ToPeso(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);

    /// <summary>Hacia arriba al peso (el IBC).</summary>
    public static decimal CeilingToPeso(decimal value) => Math.Ceiling(value);

    /// <summary>Hacia arriba al múltiplo de 100 (aportes de seguridad social). Sólo se usa con acumulados del mes, que nunca son negativos.</summary>
    public static decimal CeilingToHundred(decimal value) => Math.Ceiling(value / 100m) * 100m;
}
