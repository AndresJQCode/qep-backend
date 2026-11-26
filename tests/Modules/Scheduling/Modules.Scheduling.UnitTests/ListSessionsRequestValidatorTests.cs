using FluentValidation;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-23 §1: GET sessions takes an inclusive range of tenant days, at most as long as the calendar's.
public sealed class ListSessionsRequestValidatorTests
{
    private static readonly DateOnly Monday = new(2026, 11, 23);

    private static string[] FailedFields(ListSessionsRequest request)
    {
        var error = Assert.Throws<ValidationException>(() => new ListSessionsRequestValidator().ValidateAndThrow(request));
        return [.. error.Errors.Select(failure => failure.PropertyName).Distinct()];
    }

    [Fact]
    public void BothEndsAreRequired()
    {
        Assert.Equal(["from", "to"], FailedFields(new ListSessionsRequest(null, null, null)));
        Assert.Equal(["to"], FailedFields(new ListSessionsRequest(Monday, null, null)));
        Assert.Equal(["from"], FailedFields(new ListSessionsRequest(null, Monday, null)));
    }

    [Fact]
    public void ABackwardsRangeMarksTheEnd() =>
        Assert.Equal(["to"], FailedFields(new ListSessionsRequest(Monday, Monday.AddDays(-1), null)));

    [Fact]
    public void TheRangeIsLimitedLikeTheCalendar() =>
        Assert.Equal(
            ["to"],
            FailedFields(new ListSessionsRequest(Monday, Monday.AddDays(CalendarRequestValidator.MaxDays + 1), null)));

    [Fact]
    public void AnEmptyResourceIdMarksThatField() =>
        Assert.Equal(["resourceId"], FailedFields(new ListSessionsRequest(Monday, Monday, Guid.Empty)));

    [Fact]
    public void ASingleDayAndTheLongestRangeAreAccepted()
    {
        var validator = new ListSessionsRequestValidator();

        validator.ValidateAndThrow(new ListSessionsRequest(Monday, Monday, null));
        validator.ValidateAndThrow(new ListSessionsRequest(Monday, Monday.AddDays(CalendarRequestValidator.MaxDays), Guid.CreateVersion7()));
    }
}
