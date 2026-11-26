using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

/// <summary>
/// Datos de prueba del motor. Los valores legales son literales sólo acá (los de la siembra de Hr): el motor los recibe en un
/// <see cref="LegalValueSet"/> y nunca los escribe. <see cref="ForDate"/> arma un conjunto por fecha porque las rampas
/// (recargo dominical y jornada) cambian dentro de 2026.
/// </summary>
internal static class EngineTestData
{
    public const decimal Smmlv = 1_750_905m;

    public static readonly Guid EmployeeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static LegalValueSet ForDate(DateOnly date)
    {
        if (date < new DateOnly(2026, 1, 1))
        {
            throw new ArgumentOutOfRangeException(nameof(date), date, "Only 2026 values are seeded in the test data.");
        }

        var year = new DateOnly(2026, 1, 1);
        var base2025 = new DateOnly(2025, 1, 1);

        LegalValue Value(string key, decimal value, DateOnly? from = null) => new(key, value, from ?? base2025);

        var (sundayRate, sundayFrom) = date >= new DateOnly(2026, 7, 1)
            ? (0.90m, new DateOnly(2026, 7, 1))
            : (0.80m, new DateOnly(2025, 7, 1));
        var (weeklyHours, hoursFrom) = date >= new DateOnly(2026, 7, 15)
            ? (42m, new DateOnly(2026, 7, 15))
            : (44m, new DateOnly(2025, 7, 15));

        return new LegalValueSet(
            date,
            Value("Smmlv", Smmlv, year),
            Value("TransportAllowance", 249_095m, year),
            Value("HealthEmployeeRate", 0.04m),
            Value("HealthEmployerRate", 0.085m),
            Value("PensionEmployeeRate", 0.04m),
            Value("PensionEmployerRate", 0.12m),
            Value("HighRiskPensionTotalRate", 0.26m),
            Value("ArlClassIRate", 0.00522m),
            Value("ArlClassIIRate", 0.01044m),
            Value("ArlClassIIIRate", 0.02436m),
            Value("ArlClassIVRate", 0.0435m),
            Value("ArlClassVRate", 0.0696m),
            Value("CompensationFundRate", 0.04m),
            Value("IcbfRate", 0.03m),
            Value("SenaRate", 0.02m),
            Value("SeveranceProvisionRate", 0.0833m),
            Value("PremiumProvisionRate", 0.0833m),
            Value("VacationProvisionRate", 0.0417m),
            Value("SeveranceInterestProvisionRate", 0.01m),
            Value("TransportAllowanceCapSmmlv", 2m),
            Value("NonSalaryCapRate", 0.40m),
            Value("ExonerationThresholdSmmlv", 10m),
            Value("IbcCapSmmlv", 25m),
            Value("SundaySurchargeRate", sundayRate, sundayFrom),
            Value("WeeklyHours", weeklyHours, hoursFrom),
            Value("ExtraDayRate", 0.25m),
            Value("ExtraNightRate", 0.75m),
            Value("NightSurchargeRate", 0.35m),
            new DateOnly(2025, 6, 25),
            [
                new SolidarityBracket(4m, 16m, 0.01m, year),
                new SolidarityBracket(16m, 17m, 0.012m, year),
                new SolidarityBracket(17m, 18m, 0.014m, year),
                new SolidarityBracket(18m, 19m, 0.016m, year),
                new SolidarityBracket(19m, 20m, 0.018m, year),
                new SolidarityBracket(20m, 25m, 0.02m, year),
            ]);
    }

    /// <summary>
    /// Un empleado completo de la quincena que contiene <paramref name="periodDay"/>: contrato desde 2025-01-01, un salario,
    /// centro de clase I, empresa exonerada, auxilio quincenal sobre el salario. Cada prueba lo ajusta con <c>with</c>.
    /// </summary>
    public static EngineInput Input(DateOnly periodDay, decimal monthlySalary = Smmlv)
    {
        var (start, end, half) = QuincenaCalendar.Containing(periodDay);
        return new EngineInput(
            EmployeeId,
            new PeriodInfo(start, end, half),
            new EngineSettings(TransportFrequency.Biweekly, TransportCapBase.SalaryOnly, OvertimeBase.SalaryOnly, Exonerated: true),
            ForDate(start),
            new DateOnly(2025, 1, 1),
            null,
            [new SalarySegmentSource(new DateOnly(2025, 1, 1), monthlySalary, Teleworker: false)],
            new WorkplaceInfo("Sede principal", ArlClass.I, HighRiskPension: false),
            [],
            [],
            [],
            [],
            MonthAccumulators.Empty,
            Vacations: []);
    }

    /// <summary>
    /// Unas vacaciones del empleado de prueba: 15 días hábiles, salario registrado desde 2025-01-01 y el IBC anterior tecleado.
    /// <paramref name="paidHere"/> dice si la quincena que se prueba es la que las paga.
    /// </summary>
    public static VacationInput Vacation(
        DateOnly start,
        DateOnly end,
        bool paidHere,
        decimal salary = 3_000_000m,
        decimal previousIbc = 3_000_000m,
        VacationOrigin origin = VacationOrigin.Qep,
        decimal? monthPay = null,
        VacationAdvance? advance = null) =>
        new(
            Guid.NewGuid(),
            origin,
            start,
            end,
            15,
            paidHere,
            salary,
            new DateOnly(2025, 1, 1),
            previousIbc,
            VacationIbcSource.Typed,
            monthPay,
            advance ?? VacationAdvance.Zero);

    public static OvertimeInput Overtime(SystemConceptKey key, decimal hours)
    {
        var concept = SystemConcepts.All.Single(item => item.Key == key);
        return new OvertimeInput(concept.Id, concept.Name, hours, key, null);
    }

    public static IncomeInput Income(SystemConceptKey key, decimal amount, bool salaryConstitutive)
    {
        var concept = SystemConcepts.All.Single(item => item.Key == key);
        return new IncomeInput(concept.Id, concept.Name, amount, salaryConstitutive);
    }

    /// <summary>Corre los pasos 1 a 6 en el orden del motor y devuelve lo que las reglas siguientes consumen.</summary>
    public static EarningsChain Earnings(EngineInput input)
    {
        var worked = WorkedDaysRule.Compute(input.Period, input.ContractStart, input.ContractEndedOn);
        var vacations = VacationDaysRule.Compute(input, worked);
        var salary = SalaryRule.Compute(input, worked, vacations);
        var incomes = IncomeRule.Compute(input);
        var recurring = RecurringConceptRule.Compute(input, worked, vacations);
        var salaryIncome = incomes.SalaryIncome + recurring.SalaryIncome;
        var nonSalaryIncome = incomes.NonSalaryIncome + recurring.NonSalaryIncome;
        var overtime = OvertimeRule.Compute(input, salary.SalaryInForce, input.Prior.SalaryIncome + salaryIncome);
        var month = MonthIncomeRule.Compute(input, salary.SalaryInForce, salaryIncome, overtime.Total, nonSalaryIncome);
        var transport = TransportAllowanceRule.Compute(input, worked, salary, month, vacations);
        return new EarningsChain(worked, vacations, salary, incomes, recurring, overtime, month, transport, salaryIncome, nonSalaryIncome);
    }
}

internal sealed record EarningsChain(
    WorkedDays Worked,
    VacationDaysOutcome Vacations,
    SalaryOutcome Salary,
    IncomeOutcome Incomes,
    RecurringOutcome Recurring,
    OvertimeOutcome Overtime,
    MonthIncome Month,
    TransportOutcome Transport,
    decimal SalaryIncome,
    decimal NonSalaryIncome);
