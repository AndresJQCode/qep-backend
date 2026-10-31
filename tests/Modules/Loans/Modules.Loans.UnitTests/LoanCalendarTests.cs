using System.Globalization;
using Modules.Loans.Domain;

namespace Modules.Loans.UnitTests;

public sealed class LoanCalendarTests
{
    // Regla 6 del spec: el inicio ajustado es el del ancla, no el del período anterior.
    [Theory]
    [InlineData("EndOfMonth", "2026-10-10", "2026-10-20", "2026-10-01", "2026-10-31", 31)]
    [InlineData("EndOfMonth", "2026-02-10", "2026-02-10", "2026-02-01", "2026-02-28", 28)]
    [InlineData("DisbursementDay", "2026-10-12", "2026-10-12", "2026-10-12", "2026-11-11", 31)]
    [InlineData("DisbursementDay", "2026-10-12", "2026-11-11", "2026-10-12", "2026-11-11", 31)]
    [InlineData("DisbursementDay", "2026-01-31", "2026-02-10", "2026-01-31", "2026-02-27", 28)]
    [InlineData("DisbursementDay", "2026-01-31", "2026-02-28", "2026-02-28", "2026-03-30", 31)]
    [InlineData("DisbursementDay", "2026-01-31", "2026-03-31", "2026-03-31", "2026-04-29", 30)]
    [InlineData("DisbursementDay", "2026-01-31", "2026-04-30", "2026-04-30", "2026-05-30", 31)]
    [InlineData("DisbursementDay", "2028-01-31", "2028-02-10", "2028-01-31", "2028-02-28", 29)] // año bisiesto
    [InlineData("DisbursementDay", "2028-01-31", "2028-02-29", "2028-02-29", "2028-03-30", 31)]
    public void PeriodContainingFollowsTheCutoffRule(
        string rule, string disbursedOn, string date, string start, string end, int days)
    {
        var period = LoanCalendar.PeriodContaining(
            Enum.Parse<LoanCutoffRule>(rule), Date(disbursedOn), Date(date));

        Assert.Equal(Date(start), period.Start);
        Assert.Equal(Date(end), period.End);
        Assert.Equal(days, period.Days);
    }

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);
}
