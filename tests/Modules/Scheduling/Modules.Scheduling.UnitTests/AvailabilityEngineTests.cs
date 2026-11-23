using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21 (Agenda), «Disponibilidad»: los espacios libres se calculan, no se guardan. Expande las franjas en la zona horaria del
// tenant, genera inicios cada «duración» del servicio alineados al inicio de la ventana (PD-1), descuenta festivos, cierres y sesiones,
// aplica anticipación y horizonte, y suma o publica los cupos. Función pura.
public sealed class AvailabilityEngineTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly LocationId Location = LocationId.New();
    private static readonly ResourceId Perez = ResourceId.New();
    private static readonly ResourceId Mora = ResourceId.New();
    private static readonly ResourceId Silva = ResourceId.New();
    private static readonly DateOnly Monday = new(2026, 11, 23);
    private static readonly DateTimeOffset LongBefore = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    // 08:00 de Bogotá (UTC-5) es 13:00Z.
    private static DateTimeOffset Local(DateOnly day, int hour, int minute = 0) =>
        new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero).AddHours(hour + 5).AddMinutes(minute);

    private static Service Cleaning(int duration = 30, int buffer = 0) =>
        Service.Create(ServiceId.New(), Tenant, "Limpieza", duration, buffer, true, LongBefore);

    private static AvailabilityBlock WeeklyBlock(
        Service service,
        int fromMinute = 480,
        int toMinute = 720,
        int capacity = 1,
        SessionMode? mode = null,
        bool closesOnHolidays = true,
        int day = 1,
        params ResourceId[] resources) =>
        AvailabilityBlock.Create(
            AvailabilityBlockId.New(),
            Tenant,
            Location,
            new BlockDefinition(
                "Consulta",
                BlockKind.Weekly,
                [new WeekdayHours(day, [new TimeRange(fromMinute, toMinute)])],
                OneOff: null,
                ValidFrom: null,
                ValidTo: null,
                closesOnHolidays,
                capacity,
                mode,
                AllowClientChoice: true,
                AssignmentRule.FirstFree,
                [service.Id],
                resources.Length == 0 ? [Perez] : resources),
            LongBefore);

    private static SchedulingSettings Rules(int minNotice = 0, int horizonDays = 365)
    {
        var settings = SchedulingSettings.Create(Tenant, LongBefore);
        settings.Update(ConfirmationMode.Automatic, minNotice, horizonDays, 24, false, 24, null, null, LongBefore);
        return settings;
    }

    private static AvailabilityQuery Query(
        Service service,
        IReadOnlyList<AvailabilityBlock> blocks,
        IReadOnlyList<Closure>? closures = null,
        IReadOnlyList<Session>? sessions = null,
        IReadOnlySet<DateOnly>? holidays = null,
        SchedulingSettings? rules = null,
        DateTimeOffset? now = null,
        DateOnly? from = null,
        DateOnly? to = null,
        ResourceId? onlyResource = null,
        IReadOnlySet<ResourceId>? offered = null,
        IReadOnlyDictionary<ResourceId, LocationId>? offeredAt = null) =>
        new(
            service,
            blocks,
            closures ?? [],
            sessions ?? [],
            offeredAt ?? (offered ?? new HashSet<ResourceId> { Perez, Mora, Silva }).ToDictionary(resource => resource, _ => Location),
            rules ?? Rules(),
            holidays ?? new HashSet<DateOnly>(),
            Bogota,
            from ?? Monday,
            to ?? Monday,
            now ?? LongBefore,
            onlyResource);

    private static Session SessionAt(ResourceId resource, Service service, DateTimeOffset start, int capacity = 1, int booked = 1, bool published = false)
    {
        var session = Session.Create(
            SessionId.New(), Tenant, resource, service.Id, start, start.AddMinutes(service.DurationMinutes),
            service.BufferMinutes, capacity, published, LongBefore);
        for (var count = 0; count < booked; count++)
        {
            session.Reserve(LongBefore);
        }

        return session;
    }

    [Fact]
    public void AWeeklyBlockExpandsInTheTenantTimeZone()
    {
        var service = Cleaning();

        var slots = AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service)]));

        Assert.Equal(Local(Monday, 8), slots[0].StartsAt);
        Assert.Equal(Local(Monday, 8).AddMinutes(30), slots[0].EndsAt);
        Assert.Equal(Local(Monday, 11, 30), slots[^1].StartsAt);
    }

    [Fact]
    public void StartsAreGeneratedEveryServiceDurationAlignedToTheWindow()
    {
        var service = Cleaning(duration: 45);

        var slots = AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service, fromMinute: 490, toMinute: 700)]));

        // 08:10, 08:55, 09:40, 10:25 y 11:10 (+45 = 11:55 > 11:40: ese último no cabe en la ventana que cierra 11:40).
        Assert.Equal(
            [Local(Monday, 8, 10), Local(Monday, 8, 55), Local(Monday, 9, 40), Local(Monday, 10, 25)],
            slots.Select(slot => slot.StartsAt));
    }

    [Fact]
    public void AServiceLongerThanTheWindowOffersNothing()
    {
        var service = Cleaning(duration: 60);

        Assert.Empty(AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service, fromMinute: 480, toMinute: 510)])));
    }

    [Fact]
    public void ABlockThatDoesNotOfferTheServiceIsIgnored()
    {
        var asked = Cleaning();
        var other = Cleaning();

        Assert.Empty(AvailabilityEngine.Compute(Query(asked, [WeeklyBlock(other)])));
    }

    [Fact]
    public void AHolidayClosesABlockThatClosesOnHolidaysAndOnlyThatOne()
    {
        var service = Cleaning();
        var holidays = new HashSet<DateOnly> { Monday };

        Assert.Empty(AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service, closesOnHolidays: true)], holidays: holidays)));
        Assert.NotEmpty(AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service, closesOnHolidays: false)], holidays: holidays)));
    }

    [Fact]
    public void AClosureRemovesItsRangeOnlyForItsResource()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 600, 1, null, true, 1, Perez, Mora);
        var closure = Closure.Create(
            ClosureId.New(), Tenant, Location, Perez, Local(Monday, 8), Local(Monday, 9), "Congreso", LongBefore);

        var slots = AvailabilityEngine.Compute(Query(service, [block], closures: [closure]));

        var eight = slots.Single(slot => slot.StartsAt == Local(Monday, 8));
        Assert.Equal([Mora], eight.Options.Select(option => option.ResourceId));
        var nine = slots.Single(slot => slot.StartsAt == Local(Monday, 9));
        Assert.Equal([Perez, Mora], nine.Options.Select(option => option.ResourceId));
    }

    [Fact]
    public void AWholeLocationClosureRemovesEveryResource()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 600, 1, null, true, 1, Perez, Mora);
        var closure = Closure.Create(ClosureId.New(), Tenant, Location, null, Local(Monday, 8), Local(Monday, 9), null, LongBefore);

        var slots = AvailabilityEngine.Compute(Query(service, [block], closures: [closure]));

        Assert.DoesNotContain(slots, slot => slot.StartsAt < Local(Monday, 9));
    }

    [Fact]
    public void AWholeLocationClosureLeavesTheResourcesOfAnotherLocationFree()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 600, 1, null, true, 1, Perez, Mora);
        var otherLocation = LocationId.New();
        var closure = Closure.Create(ClosureId.New(), Tenant, Location, null, Local(Monday, 8), Local(Monday, 9), null, LongBefore);
        var at = new Dictionary<ResourceId, LocationId> { [Perez] = Location, [Mora] = otherLocation };

        var slots = AvailabilityEngine.Compute(Query(service, [block], closures: [closure], offeredAt: at));

        var eight = slots.Single(slot => slot.StartsAt == Local(Monday, 8));
        Assert.Equal([Mora], eight.Options.Select(option => option.ResourceId));
    }

    [Fact]
    public void TheBufferBlocksTheNextStartButIsNotShownToTheClient()
    {
        var service = Cleaning(duration: 30, buffer: 10);
        var booked = SessionAt(Perez, service, Local(Monday, 8));

        var slots = AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service, 480, 600)], sessions: [booked]));

        // 08:00 está tomado, 08:30 cae dentro del margen (ocupa hasta 08:40) y el siguiente libre es 09:00.
        Assert.Equal(Local(Monday, 9), slots[0].StartsAt);
        Assert.Equal(Local(Monday, 9).AddMinutes(30), slots[0].EndsAt);
    }

    [Fact]
    public void ADifferentOpenSessionThatOverlapsBlocksTheStart()
    {
        var service = Cleaning(duration: 30);
        var other = Cleaning(duration: 60);
        var overlapping = SessionAt(Perez, other, Local(Monday, 8, 15), capacity: 5, booked: 1);

        var slots = AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service, 480, 660)], sessions: [overlapping]));

        Assert.DoesNotContain(slots, slot => slot.StartsAt is var start && (start == Local(Monday, 8) || start == Local(Monday, 8, 30)));
        Assert.Contains(slots, slot => slot.StartsAt == Local(Monday, 9, 30));
    }

    [Fact]
    public void ThreeResourcesWithOneSeatEachOfferThreeSeatsAtTheSameStart()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 540, 1, null, true, 1, Perez, Mora, Silva);

        var slot = AvailabilityEngine.Compute(Query(service, [block]))[0];

        Assert.Equal(3, slot.SeatsLeft);
        Assert.Equal([Perez, Mora, Silva], slot.Options.Select(option => option.ResourceId));
        Assert.All(slot.Options, option => Assert.Equal(1, option.SeatsLeft));
    }

    [Fact]
    public void OneResourceBookedLeavesTheOthersOnTheSameStart()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 540, 1, null, true, 1, Perez, Mora);
        var booked = SessionAt(Perez, service, Local(Monday, 8));

        var slot = AvailabilityEngine.Compute(Query(service, [block], sessions: [booked]))[0];

        Assert.Equal(Local(Monday, 8), slot.StartsAt);
        Assert.Equal([Mora], slot.Options.Select(option => option.ResourceId));
        Assert.Equal(1, slot.SeatsLeft);
    }

    [Fact]
    public void AFixedClassIsAWindowAsLongAsTheService()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1140, 1200, capacity: 15);

        var slots = AvailabilityEngine.Compute(Query(service, [block]));

        var slot = Assert.Single(slots);
        Assert.Equal(Local(Monday, 19), slot.StartsAt);
        Assert.Equal(15, slot.SeatsLeft);
    }

    [Fact]
    public void FirstBookingOpensJoinsAnOpenSessionOfTheSameServiceAndStart()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1260, capacity: 5, mode: SessionMode.FirstBookingOpens);
        var open = SessionAt(Perez, service, Local(Monday, 18), capacity: 5, booked: 3);

        var slots = AvailabilityEngine.Compute(Query(service, [block], sessions: [open]));

        var joined = slots.Single(slot => slot.StartsAt == Local(Monday, 18));
        Assert.Equal(2, joined.SeatsLeft);
        Assert.Equal(open.Id, Assert.Single(joined.Options).ExistingSessionId);
        var fresh = slots.Single(slot => slot.StartsAt == Local(Monday, 19));
        Assert.Equal(5, fresh.SeatsLeft);
        Assert.Null(Assert.Single(fresh.Options).ExistingSessionId);
    }

    [Fact]
    public void AFullSessionIsNotOffered()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1140, capacity: 2);
        var full = SessionAt(Perez, service, Local(Monday, 18), capacity: 2, booked: 2);

        Assert.Empty(AvailabilityEngine.Compute(Query(service, [block], sessions: [full])));
    }

    [Fact]
    public void PublishedSessionsOnlyOffersThePublishedOnes()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1260, capacity: 6, mode: SessionMode.PublishedSessions);
        var published = SessionAt(Perez, service, Local(Monday, 19), capacity: 6, booked: 2, published: true);

        var slots = AvailabilityEngine.Compute(Query(service, [block], sessions: [published]));

        var slot = Assert.Single(slots);
        Assert.Equal(Local(Monday, 19), slot.StartsAt);
        Assert.Equal(4, slot.SeatsLeft);
        Assert.Equal(published.Id, Assert.Single(slot.Options).ExistingSessionId);
    }

    [Fact]
    public void ADeactivatedPublishedSessionIsNotOffered()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1260, capacity: 6, mode: SessionMode.PublishedSessions);
        var removed = SessionAt(Perez, service, Local(Monday, 19), capacity: 6, booked: 0, published: true);
        removed.Deactivate(LongBefore);

        Assert.Empty(AvailabilityEngine.Compute(Query(service, [block], sessions: [removed])));
    }

    [Fact]
    public void AnInactiveSessionDoesNotOccupyItsResource()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1260, capacity: 1);
        var removed = SessionAt(Perez, service, Local(Monday, 18), capacity: 1, booked: 0, published: true);
        removed.Deactivate(LongBefore);

        var slots = AvailabilityEngine.Compute(Query(service, [block], sessions: [removed], onlyResource: Perez));

        Assert.Equal(Local(Monday, 18), slots[0].StartsAt);
    }

    [Fact]
    public void APublishedSessionOutsideTheWindowOfTheBlockIsNotOffered()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1140, capacity: 6, mode: SessionMode.PublishedSessions);
        var outside = SessionAt(Perez, service, Local(Monday, 21), capacity: 6, booked: 0, published: true);

        Assert.Empty(AvailabilityEngine.Compute(Query(service, [block], sessions: [outside])));
    }

    [Fact]
    public void MinNoticeCutsTheNearestStarts()
    {
        var service = Cleaning();
        var now = Local(Monday, 8);

        var slots = AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service)], rules: Rules(minNotice: 120), now: now));

        Assert.Equal(Local(Monday, 10), slots[0].StartsAt);
    }

    [Fact]
    public void TheHorizonCutsTheFarthestStarts()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service);
        var now = new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);
        var secondMonday = Monday.AddDays(7);

        var slots = AvailabilityEngine.Compute(
            Query(service, [block], rules: Rules(horizonDays: 5), now: now, from: Monday, to: secondMonday));

        Assert.NotEmpty(slots);
        Assert.All(slots, slot => Assert.True(slot.StartsAt <= now.AddDays(5)));
        Assert.DoesNotContain(slots, slot => slot.StartsAt >= Local(secondMonday, 0));
    }

    [Fact]
    public void AnArchivedOrOutOfScopeResourceIsNotOffered()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 540, 1, null, true, 1, Perez, Mora);

        var slot = AvailabilityEngine.Compute(
            Query(service, [block], offered: new HashSet<ResourceId> { Mora }))[0];

        Assert.Equal([Mora], slot.Options.Select(option => option.ResourceId));
    }

    [Fact]
    public void AskingForOneResourceOffersOnlyThatOne()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 540, 1, null, true, 1, Perez, Mora);

        var slot = AvailabilityEngine.Compute(Query(service, [block], onlyResource: Mora))[0];

        Assert.Equal([Mora], slot.Options.Select(option => option.ResourceId));
    }

    [Fact]
    public void TheSameResourceAndStartInTwoBlocksIsOfferedOnce()
    {
        var service = Cleaning();

        var slot = AvailabilityEngine.Compute(Query(service, [WeeklyBlock(service, 480, 540), WeeklyBlock(service, 480, 540)]))[0];

        Assert.Single(slot.Options);
        Assert.Equal(1, slot.SeatsLeft);
    }

    [Fact]
    public void TheSlotsComeOrderedByStartAcrossDays()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 540);

        var slots = AvailabilityEngine.Compute(Query(service, [block], from: Monday, to: Monday.AddDays(14)));

        Assert.Equal(slots.OrderBy(slot => slot.StartsAt).Select(slot => slot.StartsAt), slots.Select(slot => slot.StartsAt));
        Assert.Equal(6, slots.Count);
    }

    // Booking separates "the rules allow this start" (422 outside_rules) from "someone holds it" (409): the first question is asked with
    // IgnoreOccupancy, which keeps blocks, closures, holidays, notice and horizon and drops what the sessions hold.
    [Fact]
    public void IgnoringOccupancyStillOffersTheStartAFullSessionHolds()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 540);
        var full = SessionAt(Perez, service, Local(Monday, 8), capacity: 1, booked: 1);

        var occupied = AvailabilityEngine.Compute(Query(service, [block], sessions: [full]));
        var rulesOnly = AvailabilityEngine.Compute(Query(service, [block], sessions: [full]) with { IgnoreOccupancy = true });

        Assert.DoesNotContain(occupied, slot => slot.StartsAt == Local(Monday, 8));
        Assert.Contains(rulesOnly, slot => slot.StartsAt == Local(Monday, 8));
    }

    [Fact]
    public void IgnoringOccupancyStillOffersAPublishedSessionTheTenantRemoved()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1260, capacity: 6, mode: SessionMode.PublishedSessions);
        var removed = SessionAt(Perez, service, Local(Monday, 19), capacity: 6, booked: 0, published: true);
        removed.Deactivate(LongBefore);

        var rulesOnly = AvailabilityEngine.Compute(Query(service, [block], sessions: [removed]) with { IgnoreOccupancy = true });

        Assert.Equal(Local(Monday, 19), Assert.Single(rulesOnly).StartsAt);
    }

    [Fact]
    public void IgnoringOccupancyDoesNotOfferAnUnpublishedStartOfAPublishedSessionsBlock()
    {
        var service = Cleaning(duration: 60);
        var block = WeeklyBlock(service, 1080, 1260, capacity: 6, mode: SessionMode.PublishedSessions);

        Assert.Empty(AvailabilityEngine.Compute(Query(service, [block]) with { IgnoreOccupancy = true }));
    }

    [Fact]
    public void IgnoringOccupancyStillHonorsClosuresAndTheNotice()
    {
        var service = Cleaning();
        var block = WeeklyBlock(service, 480, 660);
        var closure = Closure.Create(ClosureId.New(), Tenant, Location, Perez, Local(Monday, 8), Local(Monday, 9), null, LongBefore);

        var slots = AvailabilityEngine.Compute(
            Query(service, [block], closures: [closure], rules: Rules(minNotice: 30), now: Local(Monday, 9, 10)) with { IgnoreOccupancy = true });

        Assert.Equal(Local(Monday, 10), slots[0].StartsAt);
    }
}
