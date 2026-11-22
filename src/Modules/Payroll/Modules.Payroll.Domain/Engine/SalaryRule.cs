namespace Modules.Payroll.Domain.Engine;

public sealed record SalarySegment(DateOnly From, DateOnly To, int Days, decimal MonthlyAmount);

/// <summary>
/// El salario de la quincena. <paramref name="SalaryInForce"/>, <paramref name="Teleworker"/> y <paramref name="SalaryRecordDate"/>
/// son los del tramo del último día cubierto (R-8); <paramref name="Raw"/> va sin redondear.
/// </summary>
public sealed record SalaryOutcome(
    PayrollLine Line,
    decimal Raw,
    decimal SalaryInForce,
    bool Teleworker,
    DateOnly SalaryRecordDate,
    IReadOnlyList<SalarySegment> Segments);

/// <summary>Paso 2 del motor: el salario por tramos y su redondeo sobre el acumulado del mes (R-5, R-7).</summary>
public static class SalaryRule
{
    private const string Formula =
        "redondeo al peso(Σ salario mensual ÷ 30 × días de cada tramo, acumulado del mes) − lo pagado en la primera quincena";

    public static SalaryOutcome Compute(EngineInput input, WorkedDays worked)
    {
        var records = input.Salaries.OrderBy(record => record.EffectiveDate).ToList();
        if (records.Count == 0 || worked.Days <= 0)
        {
            throw new InvalidOperationException("A salary needs a salary record and at least one covered day.");
        }

        var pieces = new List<(DateOnly From, DateOnly To, SalarySegmentSource Source)>();
        for (var index = 0; index < records.Count; index++)
        {
            // The first record rules from the first covered day even when it is dated later: Hr guarantees it starts with the contract.
            var from = index == 0 || records[index].EffectiveDate < worked.CoveredStart ? worked.CoveredStart : records[index].EffectiveDate;
            var to = index == records.Count - 1 ? worked.CoveredEnd : records[index + 1].EffectiveDate.AddDays(-1);
            if (to > worked.CoveredEnd)
            {
                to = worked.CoveredEnd;
            }

            if (from <= to)
            {
                pieces.Add((from, to, records[index]));
            }
        }

        // Each segment counts its real days and the last one absorbs the difference, so the segments always add up to the worked days.
        var segments = new List<SalarySegment>(pieces.Count);
        var assigned = 0;
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            var days = index == pieces.Count - 1
                ? Math.Max(0, worked.Days - assigned)
                : piece.To.DayNumber - piece.From.DayNumber + 1;
            assigned += days;
            segments.Add(new SalarySegment(piece.From, piece.To, days, piece.Source.MonthlyAmount));
        }

        // Multiply first: one division keeps exact halves exact (a repeating 2 000 000 ÷ 30 would tip a .5 the wrong way).
        var raw = segments.Sum(segment => segment.MonthlyAmount * segment.Days / 30m);
        var amount = PayrollRounding.ToPeso(input.Prior.SalaryRaw + raw) - input.Prior.Salary;

        var last = pieces[^1].Source;
        var inputs = new List<TraceInput> { new("Días trabajados", worked.Days) };
        inputs.AddRange(segments.Select((segment, position) => new TraceInput($"Tramo {position + 1}", segment)));
        inputs.Add(new TraceInput("Salario bruto acumulado de la primera quincena", input.Prior.SalaryRaw));
        inputs.Add(new TraceInput("Salario pagado en la primera quincena", input.Prior.Salary));

        var line = new PayrollLine(
            "salary",
            PayrollLineGroup.Earning,
            "Salario",
            amount,
            null,
            new LineTrace(Formula, inputs, [], null));
        return new SalaryOutcome(line, raw, last.MonthlyAmount, last.Teleworker, last.EffectiveDate, segments);
    }
}
