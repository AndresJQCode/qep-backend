using System.Globalization;

namespace Modules.Payroll.Domain.Engine;

public sealed record VacationPayOutcome(IReadOnlyList<PayrollLine> Lines, decimal Total);

/// <summary>
/// The quincena that contains the start of a vacation pays all of it (spec «Valor y línea vacation_pay»): the salary of the start
/// day (CST art. 192) ÷ 30 × the payroll days of every quincena the vacation touches. Qep vacations only; an External one was paid
/// by Siigo.
/// </summary>
public static class VacationPayRule
{
    public const string CodePrefix = "vacation_pay:";

    private const string Formula = "redondeo al peso(salario del inicio ÷ 30 × días de nómina de todas las quincenas)";
    private const string Note = "CST arts. 186 y 192; pago anticipado de todo el disfrute";

    public static VacationPayOutcome Compute(EngineInput input)
    {
        var lines = new List<PayrollLine>();
        foreach (var vacation in input.Vacations.Where(item => item.PaidHere && item.Origin == VacationOrigin.Qep))
        {
            var quincenas = VacationCalendar.PayrollDays(vacation.StartDate, vacation.EndDate);
            var days = quincenas.Sum(item => item.Days);

            // Multiply first: one division keeps exact halves exact (see SalaryRule).
            var amount = PayrollRounding.ToPeso(vacation.SalaryAtStart * days / 30m);

            var inputs = new List<TraceInput>
            {
                new("Inicio", vacation.StartDate),
                new("Fin", vacation.EndDate),
                new("Días hábiles", vacation.BusinessDays),
            };
            inputs.AddRange(quincenas.Select(item => new TraceInput(
                $"Quincena {item.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", item.Days)));
            inputs.Add(new TraceInput("Salario del inicio", vacation.SalaryAtStart));
            inputs.Add(new TraceInput("Registro del salario del inicio", vacation.SalaryRecordDate));

            lines.Add(new PayrollLine(
                CodePrefix + vacation.VacationId,
                PayrollLineGroup.Earning,
                "Vacaciones disfrutadas",
                amount,
                null,
                new LineTrace(Formula, inputs, [], Note)));
        }

        return new VacationPayOutcome(lines, lines.Sum(line => line.Amount));
    }
}
