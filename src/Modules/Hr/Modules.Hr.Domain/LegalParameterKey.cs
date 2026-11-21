namespace Modules.Hr.Domain;

/// <summary>Los valores legales que Hr resuelve por fecha de vigencia (spec 2026-11-20, «Valores legales»). La carga de cada año es una migración.</summary>
public enum LegalParameterKey
{
    /// <summary>Salario mínimo mensual legal vigente.</summary>
    Smmlv,

    /// <summary>El piso del salario integral (13 salarios mínimos).</summary>
    IntegralSalaryFloor,
}
