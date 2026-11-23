using FluentValidation;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Plan 2026-11-21, Tarea 8: el validador marca el campo (camelCase) antes de que el dominio, que lanza por un valor imposible, se entere.
public sealed class SchedulingAvailabilityValidatorsTests
{
    private static readonly Guid One = Guid.CreateVersion7();
    private static readonly Guid Two = Guid.CreateVersion7();

    private static string[] FailedFields<T>(IValidator<T> validator, T request)
    {
        var error = Assert.Throws<ValidationException>(() => validator.ValidateAndThrow(request));
        return [.. error.Errors.Select(failure => failure.PropertyName).Distinct()];
    }

    private static BlockRequest Weekly(
        IReadOnlyList<WeekdayHoursRequest>? weekly = null,
        IReadOnlyList<Guid>? resourceIds = null,
        IReadOnlyList<Guid>? serviceIds = null,
        string? name = "Mañanas",
        int? capacity = 1,
        string? sessionMode = null,
        DateOnly? validFrom = null,
        DateOnly? validTo = null) =>
        new(
            name,
            "Weekly",
            weekly ?? [new WeekdayHoursRequest(1, [new TimeRangeRequest(480, 720)])],
            null,
            validFrom,
            validTo,
            false,
            capacity,
            sessionMode,
            false,
            null,
            serviceIds ?? [One],
            resourceIds ?? [Two]);

    private static BlockRequest OneOff(DateHoursRequest? oneOff) =>
        new("Jornada", "OneOff", null, oneOff, null, null, null, null, null, null, null, [One], [Two]);

    [Fact]
    public void AWeeklyBlockWithItsDefaultsIsValid() => new BlockRequestValidator().ValidateAndThrow(Weekly());

    [Fact]
    public void AOneOffBlockWithADateIsValid() =>
        new BlockRequestValidator().ValidateAndThrow(
            OneOff(new DateHoursRequest(new DateOnly(2026, 12, 20), [new TimeRangeRequest(540, 720)])));

    [Fact]
    public void ABlockNeedsANameAndAKnownKind() =>
        Assert.Equal(
            ["name", "kind"],
            FailedFields(new BlockRequestValidator(), Weekly(name: " ") with { Kind = "Daily" }));

    [Fact]
    public void ABlockNeedsAtLeastOneResourceAndOneService() =>
        Assert.Equal(
            ["serviceIds", "resourceIds"],
            FailedFields(new BlockRequestValidator(), Weekly(resourceIds: [], serviceIds: [])));

    [Fact]
    public void ABlockCannotRepeatOrBlankItsIds() =>
        Assert.Equal(
            ["serviceIds", "resourceIds"],
            FailedFields(new BlockRequestValidator(), Weekly(resourceIds: [One, One], serviceIds: [Guid.Empty])));

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void TheCapacityGoesFromOneTo500(int capacity) =>
        Assert.Equal(["capacityPerResource"], FailedFields(new BlockRequestValidator(), Weekly(capacity: capacity)));

    [Fact]
    public void ASessionModeMustBeAKnownName() =>
        Assert.Equal(["sessionMode"], FailedFields(new BlockRequestValidator(), Weekly(capacity: 5, sessionMode: "Published")));

    [Fact]
    public void TheValidityCannotRunBackwards() =>
        Assert.Equal(
            ["validTo"],
            FailedFields(
                new BlockRequestValidator(), Weekly(validFrom: new DateOnly(2026, 12, 20), validTo: new DateOnly(2026, 12, 1))));

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void AWeekdayGoesFromOneToSeven(int day) =>
        Assert.Equal(
            ["weekly"],
            FailedFields(
                new BlockRequestValidator(), Weekly(weekly: [new WeekdayHoursRequest(day, [new TimeRangeRequest(480, 720)])])));

    [Fact]
    public void AWeekdayCannotRepeat() =>
        Assert.Equal(
            ["weekly"],
            FailedFields(
                new BlockRequestValidator(),
                Weekly(weekly:
                [
                    new WeekdayHoursRequest(1, [new TimeRangeRequest(480, 720)]),
                    new WeekdayHoursRequest(1, [new TimeRangeRequest(840, 1080)]),
                ])));

    [Theory]
    [InlineData(720, 480)]
    [InlineData(480, 480)]
    [InlineData(-1, 60)]
    [InlineData(0, 1441)]
    public void ARangeMustRunForwardInsideTheDay(int from, int to) =>
        Assert.Equal(
            ["weekly"],
            FailedFields(
                new BlockRequestValidator(), Weekly(weekly: [new WeekdayHoursRequest(1, [new TimeRangeRequest(from, to)])])));

    [Fact]
    public void TheRangesOfADayCannotOverlap() =>
        Assert.Equal(
            ["weekly"],
            FailedFields(
                new BlockRequestValidator(),
                Weekly(weekly: [new WeekdayHoursRequest(1, [new TimeRangeRequest(480, 720), new TimeRangeRequest(700, 900)])])));

    [Fact]
    public void TwoRangesThatOnlyTouchAreValid() =>
        new BlockRequestValidator().ValidateAndThrow(
            Weekly(weekly: [new WeekdayHoursRequest(1, [new TimeRangeRequest(480, 720), new TimeRangeRequest(720, 900)])]));

    [Fact]
    public void AWeeklyBlockNeedsItsDaysAndCannotCarryADate() =>
        Assert.Equal(
            ["weekly", "oneOff"],
            FailedFields(
                new BlockRequestValidator(),
                Weekly(weekly: []) with { OneOff = new DateHoursRequest(new DateOnly(2026, 12, 20), [new TimeRangeRequest(540, 720)]) }));

    [Fact]
    public void AOneOffBlockNeedsADateAndItsRanges() =>
        Assert.Equal(["oneOff"], FailedFields(new BlockRequestValidator(), OneOff(new DateHoursRequest(null, []))));

    [Fact]
    public void AClosureNeedsItsInstantsAndMustEndAfterItStarts()
    {
        var start = new DateTimeOffset(2026, 12, 1, 13, 0, 0, TimeSpan.Zero);

        Assert.Equal(["startsAt", "endsAt"], FailedFields(new ClosureRequestValidator(), new ClosureRequest(null, null, null, null, null)));
        Assert.Equal(["endsAt"], FailedFields(new ClosureRequestValidator(), new ClosureRequest(null, null, start, start, null)));
        new ClosureRequestValidator().ValidateAndThrow(new ClosureRequest(null, One, start, start.AddHours(1), "Vacaciones"));
    }

    [Fact]
    public void AClosureReasonCannotPass200CharactersAndItsIdsCannotBeEmpty() =>
        Assert.Equal(
            ["locationId", "resourceId", "reason"],
            FailedFields(
                new ClosureRequestValidator(),
                new ClosureRequest(
                    Guid.Empty, Guid.Empty, new DateTimeOffset(2026, 12, 1, 13, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 12, 1, 14, 0, 0, TimeSpan.Zero), new string('x', 201))));

    [Fact]
    public void PublishingASessionNeedsItsFourFields() =>
        Assert.Equal(
            ["blockId", "resourceId", "serviceId", "startsAt"],
            FailedFields(new PublishSessionRequestValidator(), new PublishSessionRequest(null, null, null, null)));

    [Fact]
    public void PublishingASessionWithItsFieldsIsValid() =>
        new PublishSessionRequestValidator().ValidateAndThrow(
            new PublishSessionRequest(One, Two, Guid.CreateVersion7(), new DateTimeOffset(2026, 12, 1, 14, 0, 0, TimeSpan.Zero)));
}
