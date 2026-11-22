namespace Modules.Payroll.Domain.Engine;

/// <summary>La clase de riesgo ARL del centro de trabajo (I a V). Payroll no conoce el enum de Hr: el adaptador de Bootstrapper la
/// traduce por nombre. La tasa de cada clase es un valor legal (<see cref="LegalValueSet.ArlRate"/>).</summary>
public enum ArlClass
{
    I,
    II,
    III,
    IV,
    V,
}
