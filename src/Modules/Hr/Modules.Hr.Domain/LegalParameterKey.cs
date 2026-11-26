namespace Modules.Hr.Domain;

/// <summary>Los valores legales que Hr resuelve por fecha de vigencia (spec 2026-11-20, «Valores legales», y 2026-11-21 para
/// nómina, R-1). Los porcentajes se guardan como fracción (4 % es <c>0.04</c>), los topes en múltiplos de SMMLV y la jornada en
/// horas. La carga de cada año es una migración.</summary>
public enum LegalParameterKey
{
    /// <summary>Salario mínimo mensual legal vigente.</summary>
    Smmlv,

    /// <summary>El piso del salario integral (13 salarios mínimos).</summary>
    IntegralSalaryFloor,

    /// <summary>Auxilio de transporte.</summary>
    TransportAllowance,

    /// <summary>Desde cuándo el teletrabajador recibe conectividad: vale <c>1</c> y lo que importa es su <c>EffectiveFrom</c> (R-1).</summary>
    ConnectivityAllowanceSince,

    /// <summary>Aporte de salud del empleado (fracción).</summary>
    HealthEmployeeRate,

    /// <summary>Aporte de salud del empleador (fracción).</summary>
    HealthEmployerRate,

    /// <summary>Aporte de pensión del empleado (fracción).</summary>
    PensionEmployeeRate,

    /// <summary>Aporte de pensión del empleador (fracción).</summary>
    PensionEmployerRate,

    /// <summary>Pensión de alto riesgo, total empleado más empleador (fracción).</summary>
    HighRiskPensionTotalRate,

    /// <summary>ARL clase I (fracción).</summary>
    ArlClassIRate,

    /// <summary>ARL clase II (fracción).</summary>
    ArlClassIIRate,

    /// <summary>ARL clase III (fracción).</summary>
    ArlClassIIIRate,

    /// <summary>ARL clase IV (fracción).</summary>
    ArlClassIVRate,

    /// <summary>ARL clase V (fracción).</summary>
    ArlClassVRate,

    /// <summary>Caja de compensación (fracción).</summary>
    CompensationFundRate,

    /// <summary>ICBF (fracción).</summary>
    IcbfRate,

    /// <summary>SENA (fracción).</summary>
    SenaRate,

    /// <summary>Provisión de cesantías (fracción).</summary>
    SeveranceProvisionRate,

    /// <summary>Provisión de prima (fracción).</summary>
    PremiumProvisionRate,

    /// <summary>Provisión de vacaciones (fracción).</summary>
    VacationProvisionRate,

    /// <summary>Provisión de intereses a las cesantías (fracción).</summary>
    SeveranceInterestProvisionRate,

    /// <summary>Tope del auxilio de transporte, en múltiplos de SMMLV.</summary>
    TransportAllowanceCapSmmlv,

    /// <summary>Tope de pagos no salariales sobre el total (fracción).</summary>
    NonSalaryCapRate,

    /// <summary>Umbral de exoneración, en múltiplos de SMMLV.</summary>
    ExonerationThresholdSmmlv,

    /// <summary>Tope del IBC, en múltiplos de SMMLV.</summary>
    IbcCapSmmlv,

    /// <summary>Recargo dominical y festivo (fracción).</summary>
    SundaySurchargeRate,

    /// <summary>Jornada máxima semanal, en horas.</summary>
    WeeklyHours,

    /// <summary>Hora extra diurna (fracción).</summary>
    ExtraDayRate,

    /// <summary>Hora extra nocturna (fracción).</summary>
    ExtraNightRate,

    /// <summary>Recargo nocturno (fracción).</summary>
    NightSurchargeRate,
}
