using System.Globalization;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

public sealed class BillingCycleTests
{
    [Theory]
    [InlineData("2026-01-31", 1, "2026-01-31", "2026-02-28")]
    [InlineData("2026-01-31", 1, "2026-02-28", "2026-03-31")] // Review Focus 1: the anchor day is never lost
    [InlineData("2026-01-15", 6, "2026-01-15", "2026-07-15")]
    [InlineData("2024-02-29", 12, "2024-02-29", "2025-02-28")]
    [InlineData("2026-01-15", 1, "2026-03-20", "2026-04-15")] // between two anchors: the next one
    [InlineData("2026-01-15", 1, "2026-04-15", "2026-05-15")] // equal to an anchor: strictly later
    public void NextDueDateKeepsTheAnchorDayAcrossShortMonths(string first, int months, string after, string expected)
    {
        var result = BillingCycles.NextDueDate(DateOnly.Parse(first, CultureInfo.InvariantCulture), (BillingCycle)months, DateOnly.Parse(after, CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), result);
    }

    [Theory]
    [InlineData("2026-01-15", "2026-01-15", "2026-01-15")]
    [InlineData("2026-01-15", "2026-01-16", "2026-02-15")]
    [InlineData("2026-01-31", "2026-02-01", "2026-02-28")]
    [InlineData("2026-01-15", "2025-12-01", "2026-01-15")]
    public void FirstDueDateOnOrAfterReturnsTheFirstAnchorNotBeforeTheDate(string first, string date, string expected)
    {
        var result = BillingCycles.FirstDueDateOnOrAfter(DateOnly.Parse(first, CultureInfo.InvariantCulture), BillingCycle.Monthly, DateOnly.Parse(date, CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), result);
    }

    [Theory]
    [InlineData("Monthly", 1)]
    [InlineData("Semiannual", 6)]
    [InlineData("Annual", 12)]
    public void ParseReadsTheWireNames(string wire, int months) =>
        Assert.Equal((BillingCycle)months, BillingCycles.Parse(wire));

    [Theory]
    [InlineData("monthly")]
    [InlineData("Weekly")]
    [InlineData("")]
    [InlineData("1")]
    public void ParseRejectsAnythingElse(string wire)
    {
        var ex = Assert.Throws<ServiceContractsDomainException>(() => BillingCycles.Parse(wire));

        Assert.Equal(ServiceContractsErrorCodes.ServiceInvalid, ex.Code);
    }
}
