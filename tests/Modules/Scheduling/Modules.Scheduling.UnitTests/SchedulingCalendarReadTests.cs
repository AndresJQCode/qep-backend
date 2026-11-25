using FluentValidation;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

// Plan 2026-11-21, Tarea 10: la lógica pura de las lecturas del calendario y del escritorio. Las pruebas de la API verifican el alcance y
// los conteos de punta a punta; acá se fijan los límites del rango, las ventanas abiertas y las dos reglas del escritorio.
public sealed class SchedulingCalendarReadTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly LocationId Location = LocationId.New();
    private static readonly ResourceId Perez = ResourceId.New();
    private static readonly ResourceId Mora = ResourceId.New();
    private static readonly DateOnly Monday = new(2026, 11, 23);
    private static readonly DateTimeOffset LongBefore = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    // 08:00 de Bogotá (UTC-5) es 13:00Z.
    private static DateTimeOffset Local(DateOnly day, int hour, int minute = 0) =>
        new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero).AddHours(hour + 5).AddMinutes(minute);

    private static string[] FailedFields(CalendarRequest request)
    {
        var error = Assert.Throws<ValidationException>(() => new CalendarRequestValidator().ValidateAndThrow(request));
        return [.. error.Errors.Select(failure => failure.PropertyName).Distinct()];
    }

    [Fact]
    public void ACalendarNeedsADayOrARange() =>
        Assert.Equal(["date"], FailedFields(new CalendarRequest(null, null, null, null, null)));

    [Fact]
    public void ACalendarWithABackwardsRangeMarksTheEnd() =>
        Assert.Equal(["to"], FailedFields(new CalendarRequest(null, Monday, Monday.AddDays(-1), null, null)));

    [Fact]
    public void ARangeNeedsBothEnds()
    {
        Assert.Equal(["to"], FailedFields(new CalendarRequest(null, Monday, null, null, null)));
        Assert.Equal(["from"], FailedFields(new CalendarRequest(null, null, Monday, null, null)));
    }

    [Fact]
    public void ADayAndARangeTogetherAreAmbiguous() =>
        Assert.Equal(["date"], FailedFields(new CalendarRequest(Monday, Monday, Monday, null, null)));

    [Fact]
    public void ACalendarIsLimitedToAboutTwoMonths() =>
        Assert.Equal(
            ["to"],
            FailedFields(new CalendarRequest(null, Monday, Monday.AddDays(CalendarRequestValidator.MaxDays + 1), null, null)));

    [Fact]
    public void AnEmptyIdInTheFiltersMarksThatField()
    {
        Assert.Equal(["locationId"], FailedFields(new CalendarRequest(Monday, null, null, Guid.Empty, null)));
        Assert.Equal(["resourceIds"], FailedFields(new CalendarRequest(Monday, null, null, null, [Guid.Empty])));
    }

    [Fact]
    public void ADayOrAValidRangeIsAccepted()
    {
        var validator = new CalendarRequestValidator();

        validator.ValidateAndThrow(new CalendarRequest(Monday, null, null, null, null));
        validator.ValidateAndThrow(new CalendarRequest(null, Monday, Monday.AddDays(CalendarRequestValidator.MaxDays), null, null));
    }

    private static AvailabilityBlock Block(
        IReadOnlyList<WeekdayHours> weekly,
        ResourceId[] resources,
        bool closesOnHolidays = true,
        SessionMode? mode = null,
        Service? service = null)
    {
        service ??= Service.Create(ServiceId.New(), Tenant, "Limpieza", 30, 0, true, LongBefore);
        return AvailabilityBlock.Create(
            AvailabilityBlockId.New(),
            Tenant,
            Location,
            new BlockDefinition(
                "Consulta",
                BlockKind.Weekly,
                weekly,
                OneOff: null,
                ValidFrom: null,
                ValidTo: null,
                closesOnHolidays,
                // El modo de sesión sólo existe con cupo > 1 (spec 2026-11-21 de la Agenda): con cupo 1 el dominio lo descarta.
                CapacityPerResource: mode is null ? 1 : 5,
                mode,
                AllowClientChoice: true,
                AssignmentRule.FirstFree,
                [service.Id],
                resources),
            LongBefore);
    }

    private static WeekdayHours Day(int minuteFrom, int minuteTo) => new(1, [new TimeRange(minuteFrom, minuteTo)]);

    [Fact]
    public void TheWindowsOfAResourceAreTheBlockRangesInTheTenantTimeZone()
    {
        var windows = OpenWindows.For([Block([Day(480, 720)], [Perez])], [], Perez, Monday, Monday, new HashSet<DateOnly>(), Bogota);

        var window = Assert.Single(windows);
        Assert.Equal(Local(Monday, 8), window.Start);
        Assert.Equal(Local(Monday, 12), window.End);
    }

    [Fact]
    public void OverlappingAndAdjacentBlocksMergeIntoOneWindow()
    {
        var morning = Block([Day(480, 720)], [Perez]);
        var noon = Block([Day(720, 840)], [Perez]);
        var overlap = Block([Day(780, 900)], [Perez]);

        var windows = OpenWindows.For([overlap, noon, morning], [], Perez, Monday, Monday, new HashSet<DateOnly>(), Bogota);

        var window = Assert.Single(windows);
        Assert.Equal(Local(Monday, 8), window.Start);
        Assert.Equal(Local(Monday, 15), window.End);
    }

    [Fact]
    public void ABlockThatDoesNotOpenTheResourceIsIgnored() =>
        Assert.Empty(OpenWindows.For([Block([Day(480, 720)], [Mora])], [], Perez, Monday, Monday, new HashSet<DateOnly>(), Bogota));

    [Fact]
    public void AHolidayClosesTheBlocksThatCloseOnHolidaysButNotTheOthers()
    {
        var holidays = new HashSet<DateOnly> { Monday };
        var closing = Block([Day(480, 720)], [Perez]);
        var open = Block([Day(840, 900)], [Perez], closesOnHolidays: false);

        var windows = OpenWindows.For([closing, open], [], Perez, Monday, Monday, holidays, Bogota);

        var window = Assert.Single(windows);
        Assert.Equal(Local(Monday, 14), window.Start);
    }

    [Fact]
    public void ABlockOfPublishedSessionsOnlyOpensWhereASessionIsPublished()
    {
        var service = Service.Create(ServiceId.New(), Tenant, "Taller", 60, 0, true, LongBefore);
        var block = Block([Day(480, 720)], [Perez], mode: SessionMode.PublishedSessions, service: service);
        var published = Session.Create(
            SessionId.New(), Tenant, Perez, service.Id, Local(Monday, 9), Local(Monday, 10), 0, 5, isPublished: true, LongBefore);
        var removed = Session.Create(
            SessionId.New(), Tenant, Perez, service.Id, Local(Monday, 11), Local(Monday, 12), 0, 5, isPublished: true, LongBefore);
        removed.Deactivate(LongBefore);
        var openedByABooking = Session.Create(
            SessionId.New(), Tenant, Perez, service.Id, Local(Monday, 10), Local(Monday, 11), 0, 1, isPublished: false, LongBefore);

        var windows = OpenWindows.For([block], [published, removed, openedByABooking], Perez, Monday, Monday, new HashSet<DateOnly>(), Bogota);

        var window = Assert.Single(windows);
        Assert.Equal(Local(Monday, 9), window.Start);
        Assert.Equal(Local(Monday, 10), window.End);
    }

    private static Appointment AppointmentOf(AppointmentStatus status, DateTimeOffset start)
    {
        var end = start.AddMinutes(30);
        var appointment = Appointment.Create(
            AppointmentId.New(), Tenant, Location, Perez, ServiceId.New(), Guid.CreateVersion7(), SessionId.New(), null, start, end,
            status == AppointmentStatus.Pending ? AppointmentStatus.Pending : AppointmentStatus.Confirmed,
            AppointmentSource.BackOffice, null, null, null, LongBefore);
        switch (status)
        {
            case AppointmentStatus.Completed:
                appointment.MarkAttended(null, end);
                break;
            case AppointmentStatus.NoShow:
                appointment.MarkNoShow(null, end);
                break;
            case AppointmentStatus.Cancelled:
                appointment.Cancel(null, null, LongBefore);
                break;
        }

        return appointment;
    }

    // «Sin marcar» es la regla del contrato: Confirmed con ends_at < ahora. El repositorio la usa como expresión, así que la prueba la compila.
    [Fact]
    public void AnAppointmentIsUnmarkedOnlyWhenItIsConfirmedAndAlreadyOver()
    {
        var now = Local(Monday, 12);
        var unmarked = DeskRules.Unmarked(now).Compile();

        Assert.True(unmarked(AppointmentOf(AppointmentStatus.Confirmed, Local(Monday, 11))));
        Assert.False(unmarked(AppointmentOf(AppointmentStatus.Confirmed, now.AddMinutes(-30))));
        Assert.False(unmarked(AppointmentOf(AppointmentStatus.Confirmed, Local(Monday, 13))));
        Assert.False(unmarked(AppointmentOf(AppointmentStatus.Pending, Local(Monday, 11))));
        Assert.False(unmarked(AppointmentOf(AppointmentStatus.Completed, Local(Monday, 11))));
        Assert.False(unmarked(AppointmentOf(AppointmentStatus.NoShow, Local(Monday, 11))));
        Assert.False(unmarked(AppointmentOf(AppointmentStatus.Cancelled, Local(Monday, 11))));
    }

    [Fact]
    public void TheRequestAgeIsInWholeMinutesAndNeverNegative()
    {
        var now = Local(Monday, 12);

        Assert.Equal(90, DeskRules.RequestedAgoMinutes(Local(Monday, 10, 30), now));
        Assert.Equal(1, DeskRules.RequestedAgoMinutes(now.AddSeconds(-119), now));
        Assert.Equal(0, DeskRules.RequestedAgoMinutes(now.AddMinutes(5), now));
    }
}
