namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// El texto de cada valor legal en el rastro del cálculo: la etiqueta y el fundamento (la norma). Las claves son las de
/// <c>LegalParameterKey</c> de Hr y <c>SolidarityFund</c>. Una clave sin entrada lanza <see cref="KeyNotFoundException"/>: un valor
/// legal sin texto es un hueco que la prueba de cobertura debe encontrar.
/// </summary>
public static class LegalBasis
{
    private static readonly Dictionary<string, (string Label, string Basis)> Entries =
        new Dictionary<string, (string Label, string Basis)>(StringComparer.Ordinal)
        {
            ["Smmlv"] = ("Salario mínimo mensual legal vigente", "Decreto anual del salario mínimo"),
            ["IntegralSalaryFloor"] = ("Piso del salario integral", "CST art. 132, num. 2 (mod. Ley 50 de 1990, art. 18)"),
            ["TransportAllowance"] = ("Auxilio de transporte", "Ley 15/1959"),
            ["ConnectivityAllowanceSince"] = ("Auxilio de conectividad para teletrabajo", "Ley 2121/2021"),
            ["HealthEmployeeRate"] = ("Salud del empleado", "Ley 100/1993 art. 204"),
            ["HealthEmployerRate"] = ("Salud del empleador", "Ley 100/1993 art. 204"),
            ["PensionEmployeeRate"] = ("Pensión del empleado", "Ley 100/1993 art. 204"),
            ["PensionEmployerRate"] = ("Pensión del empleador", "Ley 100/1993 art. 204"),
            ["HighRiskPensionTotalRate"] = ("Pensión de alto riesgo (total)", "Decreto 2090/2003"),
            ["ArlClassIRate"] = ("ARL clase I", "Decretos 1772/1994 y 1607/2002"),
            ["ArlClassIIRate"] = ("ARL clase II", "Decretos 1772/1994 y 1607/2002"),
            ["ArlClassIIIRate"] = ("ARL clase III", "Decretos 1772/1994 y 1607/2002"),
            ["ArlClassIVRate"] = ("ARL clase IV", "Decretos 1772/1994 y 1607/2002"),
            ["ArlClassVRate"] = ("ARL clase V", "Decretos 1772/1994 y 1607/2002"),
            ["CompensationFundRate"] = ("Caja de compensación", "Ley 21/1982"),
            ["IcbfRate"] = ("ICBF", "Ley 89/1988"),
            ["SenaRate"] = ("SENA", "Ley 119/1994"),
            ["SeveranceProvisionRate"] = ("Cesantías", "Ley 50/1990 art. 99"),
            ["PremiumProvisionRate"] = ("Prima", "CST art. 306"),
            ["VacationProvisionRate"] = ("Vacaciones", "CST arts. 186 a 192"),
            ["SeveranceInterestProvisionRate"] = ("Intereses a las cesantías", "Ley 52/1975"),
            ["TransportAllowanceCapSmmlv"] = ("Tope del auxilio de transporte", "Ley 15/1959"),
            ["NonSalaryCapRate"] = ("Tope de pagos no salariales", "Ley 1393/2010 art. 30"),
            ["ExonerationThresholdSmmlv"] = ("Umbral de exoneración", "ET art. 114-1"),
            ["IbcCapSmmlv"] = ("Tope del IBC", "Ley 797/2003 art. 5"),
            ["SundaySurchargeRate"] = ("Recargo dominical y festivo", "Ley 2466/2025"),
            ["WeeklyHours"] = ("Jornada máxima semanal", "Ley 2101/2021 art. 3"),
            ["ExtraDayRate"] = ("Hora extra diurna", "CST, Ley 2466/2025"),
            ["ExtraNightRate"] = ("Hora extra nocturna", "CST, Ley 2466/2025"),
            ["NightSurchargeRate"] = ("Recargo nocturno", "CST, Ley 2466/2025"),
            ["SolidarityFund"] = ("Fondo de solidaridad pensional", "Ley 100/1993 art. 27"),
        };

    public static string LabelOf(string key) => Entries[key].Label;

    public static string BasisOf(string key) => Entries[key].Basis;
}
