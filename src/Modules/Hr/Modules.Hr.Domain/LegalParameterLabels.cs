using System.Collections.Frozen;

namespace Modules.Hr.Domain;

/// <summary>El grupo con que la consola del operador agrupa los valores legales anuales (los tramos van en su propia sección).</summary>
public enum LegalParameterGroup
{
    SalaryAndCaps,
    Allowances,
    Contributions,
    Provisions,
    WorkingTime,
}

/// <summary>La unidad de un valor legal: decide cómo se valida y cómo se dibuja.</summary>
public enum LegalParameterUnit
{
    /// <summary>Un monto en pesos.</summary>
    Currency,

    /// <summary>Una tasa guardada como fracción (4 % es <c>0.04</c>).</summary>
    Fraction,

    /// <summary>Un múltiplo de SMMLV.</summary>
    SmmlvMultiple,

    /// <summary>Horas.</summary>
    Hours,

    /// <summary>Una bandera que vale exactamente <c>1</c>: lo que importa es desde cuándo rige.</summary>
    Flag,
}

/// <summary>El nombre en español, el grupo, la unidad y si exige fila cada año.</summary>
public sealed record LegalParameterLabel(string Name, LegalParameterGroup Group, LegalParameterUnit Unit, bool Yearly);

/// <summary>
/// La <b>única</b> fuente de nombre, grupo y unidad de los valores legales en Hr. Los nombres se copian de <c>LegalBasis</c> (Payroll)
/// para que la consola y el rastro de nómina digan lo mismo: Hr no puede referenciar a Payroll, y una prueba de Bootstrapper compara
/// las dos listas. Una clave sin entrada lanza <see cref="KeyNotFoundException"/>: es un hueco que la prueba de cobertura encuentra.
/// </summary>
public static class LegalParameterLabels
{
    private const LegalParameterGroup Salary = LegalParameterGroup.SalaryAndCaps;
    private const LegalParameterGroup Allow = LegalParameterGroup.Allowances;
    private const LegalParameterGroup Contrib = LegalParameterGroup.Contributions;
    private const LegalParameterGroup Prov = LegalParameterGroup.Provisions;
    private const LegalParameterGroup Time = LegalParameterGroup.WorkingTime;

    private const string SolidarityFundName = "Fondo de solidaridad pensional";

    private static readonly FrozenDictionary<LegalParameterKey, LegalParameterLabel> Entries =
        new Dictionary<LegalParameterKey, LegalParameterLabel>
        {
            [LegalParameterKey.Smmlv] = new("Salario mínimo mensual legal vigente", Salary, LegalParameterUnit.Currency, true),
            [LegalParameterKey.IntegralSalaryFloor] = new("Piso del salario integral", Salary, LegalParameterUnit.Currency, true),
            [LegalParameterKey.TransportAllowance] = new("Auxilio de transporte", Allow, LegalParameterUnit.Currency, true),
            [LegalParameterKey.ConnectivityAllowanceSince] = new("Auxilio de conectividad para teletrabajo", Allow, LegalParameterUnit.Flag, false),
            [LegalParameterKey.HealthEmployeeRate] = new("Salud del empleado", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.HealthEmployerRate] = new("Salud del empleador", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.PensionEmployeeRate] = new("Pensión del empleado", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.PensionEmployerRate] = new("Pensión del empleador", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.HighRiskPensionTotalRate] = new("Pensión de alto riesgo (total)", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.ArlClassIRate] = new("ARL clase I", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.ArlClassIIRate] = new("ARL clase II", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.ArlClassIIIRate] = new("ARL clase III", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.ArlClassIVRate] = new("ARL clase IV", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.ArlClassVRate] = new("ARL clase V", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.CompensationFundRate] = new("Caja de compensación", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.IcbfRate] = new("ICBF", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.SenaRate] = new("SENA", Contrib, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.SeveranceProvisionRate] = new("Cesantías", Prov, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.PremiumProvisionRate] = new("Prima", Prov, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.VacationProvisionRate] = new("Vacaciones", Prov, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.SeveranceInterestProvisionRate] = new("Intereses a las cesantías", Prov, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.TransportAllowanceCapSmmlv] = new("Tope del auxilio de transporte", Salary, LegalParameterUnit.SmmlvMultiple, true),
            [LegalParameterKey.NonSalaryCapRate] = new("Tope de pagos no salariales", Salary, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.ExonerationThresholdSmmlv] = new("Umbral de exoneración", Salary, LegalParameterUnit.SmmlvMultiple, true),
            [LegalParameterKey.IbcCapSmmlv] = new("Tope del IBC", Salary, LegalParameterUnit.SmmlvMultiple, true),
            [LegalParameterKey.SundaySurchargeRate] = new("Recargo dominical y festivo", Time, LegalParameterUnit.Fraction, false),
            [LegalParameterKey.WeeklyHours] = new("Jornada máxima semanal", Time, LegalParameterUnit.Hours, false),
            [LegalParameterKey.ExtraDayRate] = new("Hora extra diurna", Time, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.ExtraNightRate] = new("Hora extra nocturna", Time, LegalParameterUnit.Fraction, true),
            [LegalParameterKey.NightSurchargeRate] = new("Recargo nocturno", Time, LegalParameterUnit.Fraction, true),
        }.ToFrozenDictionary();

    /// <summary>Las 27 claves que exigen fila cada año, en el orden de <see cref="LegalParameterKey"/>.</summary>
    public static IReadOnlyList<LegalParameterKey> YearlyKeys { get; } =
        Enum.GetValues<LegalParameterKey>().Where(key => Entries[key].Yearly).ToArray();

    public static LegalParameterLabel Of(LegalParameterKey key) => Entries[key];

    public static string NameOf(LegalBracketKey key) => key switch
    {
        LegalBracketKey.SolidarityFund => SolidarityFundName,
        _ => throw new KeyNotFoundException($"El conjunto de tramos {key} no tiene nombre."),
    };
}
