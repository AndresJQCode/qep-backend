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

    private static CreateServiceCommand Command(DateOnly firstDueDate, string cycle = "Monthly", decimal price = 100m, string name = "Hosting") =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, price, null, cycle, firstDueDate, null, null);

    private static bool Fails(CreateServiceCommand command, string property) =>
        new CreateServiceValidator(new FixedClock()).Validate(command).Errors.Any(error => error.PropertyName == property);

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

    [Fact]
    public void TheNameCannotExceedTheColumn()
    {
        Assert.True(Fails(Command(new DateOnly(2026, 11, 1), name: new string('x', 151)), nameof(CreateServiceCommand.Name)));
        Assert.False(Fails(Command(new DateOnly(2026, 11, 1), name: new string('x', 150)), nameof(CreateServiceCommand.Name)));
    }
}
