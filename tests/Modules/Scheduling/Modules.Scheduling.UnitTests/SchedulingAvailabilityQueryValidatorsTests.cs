using FluentValidation;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Plan 2026-11-21, Tarea 9 (availability and customer lookup): the validator marks the field (camelCase) before the handler loads anything, and the 422 carries the `errors` map.
public sealed class SchedulingAvailabilityQueryValidatorsTests
{
    private static readonly DateOnly Day = new(2026, 12, 7);

    private static string[] FailedFields<T>(IValidator<T> validator, T request)
    {
        var error = Assert.Throws<ValidationException>(() => validator.ValidateAndThrow(request));
        return [.. error.Errors.Select(failure => failure.PropertyName).Distinct()];
    }

    [Fact]
    public void AvailabilityNeedsAServiceAndBothDates() =>
        Assert.Equal(
            ["serviceId", "from", "to"],
            FailedFields(new AvailabilityRequestValidator(), new AvailabilityRequest(null, null, null, null)));

    [Fact]
    public void AvailabilityCannotEndBeforeItStarts() =>
        Assert.Equal(
            ["to"],
            FailedFields(new AvailabilityRequestValidator(), new AvailabilityRequest(Guid.CreateVersion7(), Day, Day.AddDays(-1), null)));

    [Fact]
    public void AvailabilityIsLimitedToAboutTwoMonths() =>
        Assert.Equal(
            ["to"],
            FailedFields(
                new AvailabilityRequestValidator(),
                new AvailabilityRequest(Guid.CreateVersion7(), Day, Day.AddDays(AvailabilityRequestValidator.MaxDays + 1), null)));

    [Fact]
    public void AvailabilityOfASingleDayWithoutAResourceIsValid() =>
        new AvailabilityRequestValidator().ValidateAndThrow(new AvailabilityRequest(Guid.CreateVersion7(), Day, Day, null));

    [Theory]
    [InlineData(null, null, "name")]
    [InlineData("  ", null, "name")]
    public void ANewCustomerNeedsAName(string? name, string? phone, string field) =>
        Assert.Equal([field], FailedFields(new NewCustomerRequestValidator(), new NewCustomerRequest(name, phone)));

    [Fact]
    public void ANewCustomerPhoneCannotPass32Characters() =>
        Assert.Equal(
            ["phone"], FailedFields(new NewCustomerRequestValidator(), new NewCustomerRequest("Ana Ruiz", new string('3', 33))));

    [Fact]
    public void ANewCustomerWithoutAPhoneIsValid() =>
        new NewCustomerRequestValidator().ValidateAndThrow(new NewCustomerRequest("Ana Ruiz", null));
}
