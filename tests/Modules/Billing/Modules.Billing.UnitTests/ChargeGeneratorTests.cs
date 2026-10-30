using System.Globalization;
using Modules.Billing.Domain;

namespace Modules.Billing.UnitTests;

public sealed class ChargeGeneratorTests
{
    private static readonly DateOnly Today = new(2026, 3, 10);

    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static BillingService NewService(string first, BillingCycle cycle = BillingCycle.Monthly, decimal price = 100m, string currency = "COP") =>
        BillingService.Create(
            BillingServiceId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", price, currency,
            cycle, DateOnly.Parse(first, CultureInfo.InvariantCulture), Now);

    [Fact]
    public void AFutureFirstDueDateGeneratesExactlyOneCharge()
    {
        var service = NewService("2026-06-01");

        var charges = ChargeGenerator.Generate(service, Today, Now);

        var charge = Assert.Single(charges);
        Assert.Equal(new DateOnly(2026, 6, 1), charge.DueDate);
        Assert.Equal(new DateOnly(2026, 6, 1), service.LastGeneratedDueDate);
        Assert.Empty(ChargeGenerator.Generate(service, Today, Now));
    }

    [Fact]
    public void APastFirstDueDateGeneratesEveryPeriodUpToTheWindow()
    {
        var charges = ChargeGenerator.Generate(NewService("2025-12-10"), Today, Now);

        Assert.Equal(
            [new DateOnly(2025, 12, 10), new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 10), new DateOnly(2026, 3, 10)],
            charges.Select(c => c.DueDate));
    }

    [Fact]
    public void ADueDateInsideTheWindowIsGenerated()
    {
        var service = NewService("2026-01-14");
        service.MarkGenerated(new DateOnly(2026, 2, 14));

        var charges = ChargeGenerator.Generate(service, Today, Now);

        Assert.Equal([new DateOnly(2026, 3, 14)], charges.Select(c => c.DueDate));
    }

    [Fact]
    public void ADueDateJustOutsideTheWindowIsNotGeneratedAgainstAnExistingHistory()
    {
        var service = NewService("2026-01-16");
        service.MarkGenerated(new DateOnly(2026, 2, 16));

        Assert.Empty(ChargeGenerator.Generate(service, Today, Now));
    }

    [Fact]
    public void NothingIsGeneratedForAPausedOrCancelledService()
    {
        var paused = NewService("2026-01-10");
        paused.Pause(Now);
        var cancelled = NewService("2026-01-10");
        cancelled.Cancel(Now);

        Assert.Empty(ChargeGenerator.Generate(paused, Today, Now));
        Assert.Empty(ChargeGenerator.Generate(cancelled, Today, Now));
    }

    [Fact]
    public void GeneratedChargesCopyTheCurrentPriceAndCurrency()
    {
        var service = NewService("2026-03-10", price: 345.5m, currency: "USD");

        var charge = Assert.Single(ChargeGenerator.Generate(service, Today, Now));

        Assert.Equal(345.5m, charge.Amount);
        Assert.Equal("USD", charge.Currency);
    }

    // Task 2 diferido: NextDueDateToGenerate ignora ReactivatedOn mientras LastGeneratedDueDate sea null. Es inalcanzable porque
    // el alta genera de inmediato; esta prueba fija que Generate deja siempre una marca, caiga donde caiga el primer vencimiento.
    [Theory]
    [InlineData(-400)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(400)]
    public void GeneratingRightAfterCreationAlwaysLeavesTheServiceWithAGeneratedMark(int daysFromToday)
    {
        var service = NewService(Today.AddDays(daysFromToday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var charges = ChargeGenerator.Generate(service, Today, Now);

        Assert.NotEmpty(charges);
        Assert.NotNull(service.LastGeneratedDueDate);
        Assert.Equal(charges[^1].DueDate, service.LastGeneratedDueDate);
    }
}
