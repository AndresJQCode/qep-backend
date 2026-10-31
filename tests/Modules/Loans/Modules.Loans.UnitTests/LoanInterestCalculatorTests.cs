using System.Globalization;
using Modules.Loans.Domain;

namespace Modules.Loans.UnitTests;

public sealed class LoanInterestCalculatorTests
{
    // `changes` es "fecha:saldo,fecha:saldo"; los tramos esperados, "inicio..fin:capital:días:interés|...".
    [Theory]
    // Regla 4 del spec, el ejemplo de octubre.
    [InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-31", "100000000", "2026-10-15:60000000", "1.5",
        "2026-10-01..2026-10-14:100000000:14:677419.35|2026-10-15..2026-10-31:60000000:17:493548.39", "1170967.74")]
    // Primer período parcial: el día del desembolso cuenta y el divisor es el período completo.
    [InlineData("2026-10-01", "2026-10-31", "2026-10-10", "2026-10-31", "100000000", "", "1.5",
        "2026-10-10..2026-10-31:100000000:22:1064516.13", "1064516.13")]
    // Febrero: el divisor es 28.
    [InlineData("2026-02-01", "2026-02-28", "2026-02-01", "2026-02-28", "100000000", "2026-02-15:60000000", "1.5",
        "2026-02-01..2026-02-14:100000000:14:750000.00|2026-02-15..2026-02-28:60000000:14:450000.00", "1200000.00")]
    // Desembolso un 31: 31 ene al 27 feb, 28 días.
    [InlineData("2026-01-31", "2026-02-27", "2026-01-31", "2026-02-27", "100000000", "", "1.5",
        "2026-01-31..2026-02-27:100000000:28:1500000.00", "1500000.00")]
    // Desembolso un 31, segundo período: 28 feb al 30 mar, 31 días, con abono.
    [InlineData("2026-02-28", "2026-03-30", "2026-02-28", "2026-03-30", "100000000", "2026-03-10:60000000", "1.5",
        "2026-02-28..2026-03-09:100000000:10:483870.97|2026-03-10..2026-03-30:60000000:21:609677.42", "1093548.39")]
    // Saldar el 15: se calcula hasta el 14; el cambio a cero queda fuera del rango.
    [InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-14", "100000000", "2026-10-15:0", "1.5",
        "2026-10-01..2026-10-14:100000000:14:677419.35", "677419.35")]
    // Dos abonos el mismo día: gana el último saldo, sin tramo de cero días.
    [InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-31", "100000000", "2026-10-15:90000000,2026-10-15:60000000", "1.5",
        "2026-10-01..2026-10-14:100000000:14:677419.35|2026-10-15..2026-10-31:60000000:17:493548.39", "1170967.74")]
    // Abono el primer día: un solo tramo con el capital nuevo.
    [InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-31", "100000000", "2026-10-01:60000000", "1.5",
        "2026-10-01..2026-10-31:60000000:31:900000.00", "900000.00")]
    // Causado a hoy, 20 de octubre.
    [InlineData("2026-10-01", "2026-10-31", "2026-10-01", "2026-10-20", "100000000", "2026-10-15:60000000", "1.5",
        "2026-10-01..2026-10-14:100000000:14:677419.35|2026-10-15..2026-10-20:60000000:6:174193.55", "851612.90")]
    // Punto medio: 0,505 sube a 0,51; el redondeo bancario daría 0,50.
    [InlineData("2026-04-01", "2026-04-30", "2026-04-01", "2026-04-30", "1.01", "", "50",
        "2026-04-01..2026-04-30:1.01:30:0.51", "0.51")]
    public void SegmentsSplitThePeriodAndAddUp(
        string start, string end, string from, string to, string opening, string changes, string rate,
        string expectedSegments, string expectedTotal)
    {
        var period = new LoanPeriod(Date(start), Date(end));

        var segments = LoanInterestCalculator.Segments(
            period, Date(from), Date(to), Dec(opening), ParseChanges(changes), Dec(rate));

        Assert.Equal(expectedSegments, string.Join('|', segments.Select(Format)));
        Assert.Equal(Dec(expectedTotal), LoanInterestCalculator.Total(segments));
    }

    [Fact]
    public void AnEmptyRangeHasNoSegments()
    {
        // Saldar el primer día deja el rango al revés (from > to).
        var period = new LoanPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        var segments = LoanInterestCalculator.Segments(
            period, new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 30), 100_000_000m, [], 1.5m);

        Assert.Empty(segments);
        Assert.Equal(0m, LoanInterestCalculator.Total(segments));
    }

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    private static decimal Dec(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static List<CapitalChange> ParseChanges(string changes) =>
        changes.Length == 0
            ? []
            : changes.Split(',')
                .Select(pair => pair.Split(':'))
                .Select(parts => new CapitalChange(Date(parts[0]), Dec(parts[1])))
                .ToList();

    private static string Format(InterestSegment s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s.Start:yyyy-MM-dd}..{s.End:yyyy-MM-dd}:{s.Principal}:{s.Days}:{s.Interest:0.00}");
}
