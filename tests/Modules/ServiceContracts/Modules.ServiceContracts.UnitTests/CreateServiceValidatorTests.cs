using BuildingBlocks.Application;
using Modules.ServiceContracts.Application;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>
/// Los límites de <c>firstDueDate</c>: <c>BillingCycles.NextDueDate</c> recorre las anclas una a una, así que una fecha de
/// hace siglos o un alta con miles de cobros no pueden pasar. Piso 2000-01-01; techo hoy + 5 años.
/// </summary>
public sealed class CreateServiceValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static CreateServiceCommand Command(
        DateOnly firstDueDate,
        string cycle = "Monthly",
        decimal price = 100m,
        string name = "Hosting",
        PriceReviewInput? priceReview = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, price, null, cycle, firstDueDate, null, null, priceReview);

    // 02:00 UTC del 30 es todavía el 29 en Bogotá: así se distingue la «hoy» del tenant de la fecha UTC.
    private static CreateServiceValidator Validator() =>
        new(new FixedClock(), new FixedTenantClock(new DateTimeOffset(2026, 10, 30, 2, 0, 0, TimeSpan.Zero)));

    private static bool Fails(CreateServiceCommand command, string property) =>
        Validator().Validate(command).Errors.Any(error => error.PropertyName == property);

    // Una revisión a siglos de hoy desbordaría la aritmética de fechas y tumbaría toda lectura del tenant que calcule revisiones.
    [Theory]
    [InlineData("2027-01-10", false)]
    [InlineData("2036-10-29", false)]
    [InlineData("2036-10-30", true)]
    [InlineData("9999-12-31", true)]
    [InlineData(null, false)]
    public async Task TheNextReviewDateIsAtMostTenYearsAfterTheTenantToday(string? date, bool rejected)
    {
        var review = date is null ? (DateOnly?)null : DateOnly.ParseExact(date, "yyyy-MM-dd");
        var command = Command(new DateOnly(2026, 11, 1), priceReview: new PriceReviewInput("Ipc", 0m, review));

        var result = await Validator().ValidateAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(rejected, result.Errors.Any(error => error.PropertyName == "PriceReview.NextReviewDate"));
    }

    [Theory]
    [InlineData("1999-12-31", true)]
    [InlineData("2000-01-01", false)]
    [InlineData("2026-10-29", false)]
    [InlineData("2031-10-29", false)]
    [InlineData("2031-10-30", true)]
    [InlineData("0001-01-01", true)]
    public void TheFirstDueDateIsBoundedOnBothSides(string date, bool rejected)
    {
        var command = Command(DateOnly.ParseExact(date, "yyyy-MM-dd"));

        Assert.Equal(rejected, Fails(command, nameof(CreateServiceCommand.FirstDueDate)));
    }

    [Theory]
    [InlineData("Monthly", false)]
    [InlineData("Semiannual", false)]
    [InlineData("Annual", false)]
    [InlineData("monthly", true)]
    [InlineData("1", true)]
    [InlineData("", true)]
    public void TheCycleIsOneOfTheThreeWireNames(string cycle, bool rejected)
    {
        Assert.Equal(rejected, Fails(Command(new DateOnly(2026, 11, 1), cycle), nameof(CreateServiceCommand.Cycle)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(0.01, false)]
    [InlineData(150000, false)]
    public void ThePriceMustBePositive(double price, bool rejected)
    {
        Assert.Equal(rejected, Fails(Command(new DateOnly(2026, 11, 1), price: (decimal)price), nameof(CreateServiceCommand.Price)));
    }

    [Fact]
    public void ThePriceCannotOverflowTheColumnOrCarryMoreThanTwoDecimals()
    {
        Assert.True(Fails(Command(new DateOnly(2026, 11, 1), price: 10_000_000_000_000_000m), nameof(CreateServiceCommand.Price)));
        Assert.True(Fails(Command(new DateOnly(2026, 11, 1), price: 1.005m), nameof(CreateServiceCommand.Price)));
        Assert.False(Fails(Command(new DateOnly(2026, 11, 1), price: 9_999_999_999_999_999.99m), nameof(CreateServiceCommand.Price)));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Hosting", false)]
    public void TheNameMustHaveSomethingAfterTrimming(string name, bool rejected)
    {
        Assert.Equal(rejected, Fails(Command(new DateOnly(2026, 11, 1), name: name), nameof(CreateServiceCommand.Name)));
    }

    [Theory]
    [InlineData("Manual", false)]
    [InlineData("Ipc", false)]
    [InlineData("ipc", true)]
    [InlineData("Annual", true)]
    [InlineData("2", true)]
    [InlineData(null, true)]
    public void TheAdjustmentModeIsOneOfTheTwoWireNames(string? mode, bool rejected)
    {
        var command = Command(new DateOnly(2026, 11, 1), priceReview: new PriceReviewInput(mode, 0m, null));

        Assert.Equal(rejected, Fails(command, "PriceReview.AdjustmentMode"));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("2.50", false)]
    [InlineData("50", false)]
    [InlineData("50.01", true)]
    [InlineData("-0.01", true)]
    [InlineData("1.005", true)]
    [InlineData(null, true)]
    public void TheExtraPointsAreRequiredAndBounded(string? points, bool rejected)
    {
        var value = points is null ? (decimal?)null : decimal.Parse(points, System.Globalization.CultureInfo.InvariantCulture);
        var command = Command(new DateOnly(2026, 11, 1), priceReview: new PriceReviewInput("Ipc", value, null));

        Assert.Equal(rejected, Fails(command, "PriceReview.IpcExtraPoints"));
    }

    [Fact]
    public void AnAbsentPriceReviewPasses() =>
        Assert.True(Validator().Validate(Command(new DateOnly(2026, 11, 1))).IsValid);

    [Fact]
    public void TheNameCannotExceedTheColumn()
    {
        Assert.True(Fails(Command(new DateOnly(2026, 11, 1), name: new string('x', 151)), nameof(CreateServiceCommand.Name)));
        Assert.False(Fails(Command(new DateOnly(2026, 11, 1), name: new string('x', 150)), nameof(CreateServiceCommand.Name)));
    }
}
