namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Todos los valores legales que el motor lee, resueltos al primer día del período. Es la «foto legal» del período: serializable
/// con <c>System.Text.Json</c> sin configuración. Ningún valor está escrito en el código: salen de las tablas de Hr.
/// </summary>
public sealed record LegalValueSet(
    DateOnly ResolvedAt,
    LegalValue Smmlv,
    LegalValue TransportAllowance,
    LegalValue HealthEmployeeRate,
    LegalValue HealthEmployerRate,
    LegalValue PensionEmployeeRate,
    LegalValue PensionEmployerRate,
    LegalValue HighRiskPensionTotalRate,
    LegalValue ArlClassIRate,
    LegalValue ArlClassIIRate,
    LegalValue ArlClassIIIRate,
    LegalValue ArlClassIVRate,
    LegalValue ArlClassVRate,
    LegalValue CompensationFundRate,
    LegalValue IcbfRate,
    LegalValue SenaRate,
    LegalValue SeveranceProvisionRate,
    LegalValue PremiumProvisionRate,
    LegalValue VacationProvisionRate,
    LegalValue SeveranceInterestProvisionRate,
    LegalValue TransportAllowanceCapSmmlv,
    LegalValue NonSalaryCapRate,
    LegalValue ExonerationThresholdSmmlv,
    LegalValue IbcCapSmmlv,
    LegalValue SundaySurchargeRate,
    LegalValue WeeklyHours,
    LegalValue ExtraDayRate,
    LegalValue ExtraNightRate,
    LegalValue NightSurchargeRate,
    DateOnly? ConnectivityAllowanceSince,
    IReadOnlyList<SolidarityBracket> SolidarityBrackets)
{
    /// <summary>La tasa de ARL de la clase de riesgo del centro de trabajo.</summary>
    public LegalValue ArlRate(ArlClass riskClass) => riskClass switch
    {
        ArlClass.I => ArlClassIRate,
        ArlClass.II => ArlClassIIRate,
        ArlClass.III => ArlClassIIIRate,
        ArlClass.IV => ArlClassIVRate,
        ArlClass.V => ArlClassVRate,
        _ => throw new ArgumentOutOfRangeException(nameof(riskClass), riskClass, "Unknown ARL class."),
    };

    /// <summary>
    /// La tasa del fondo de solidaridad para un ingreso de <paramref name="smmlvMultiple"/> salarios mínimos. El límite inferior es
    /// inclusivo y el superior no, salvo el último tramo, que incluye su tope. Fuera de todo tramo ⇒ <c>null</c> (R-9).
    /// </summary>
    public decimal? SolidarityRate(decimal smmlvMultiple)
    {
        var last = SolidarityBrackets.Count == 0 ? null : SolidarityBrackets.MaxBy(bracket => bracket.LowerSmmlv);
        foreach (var bracket in SolidarityBrackets)
        {
            if (smmlvMultiple < bracket.LowerSmmlv)
            {
                continue;
            }

            if (bracket.UpperSmmlv is null
                || smmlvMultiple < bracket.UpperSmmlv
                || (ReferenceEquals(bracket, last) && smmlvMultiple <= bracket.UpperSmmlv))
            {
                return bracket.Rate;
            }
        }

        return null;
    }
}
