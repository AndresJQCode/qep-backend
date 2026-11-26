using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21 (Agenda), «Franjas»: servicios, recursos y cupo por recurso; repetición semanal o puntual con vigencia.
public sealed class AvailabilityBlockTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly ServiceId Cleaning = ServiceId.New();
    private static readonly ResourceId Perez = ResourceId.New();
    private static readonly ResourceId Mora = ResourceId.New();

    private static BlockDefinition Weekly(
        int capacity = 1,
        SessionMode? mode = null,
        DateOnly? validFrom = null,
        DateOnly? validTo = null,
        IReadOnlyList<ServiceId>? services = null,
        IReadOnlyList<ResourceId>? resources = null) =>
        new(
            "Consulta general",
            BlockKind.Weekly,
            [new WeekdayHours(1, [new TimeRange(480, 720)]), new WeekdayHours(3, [new TimeRange(480, 720), new TimeRange(840, 1080)])],
            OneOff: null,
            validFrom,
            validTo,
            ClosesOnHolidays: true,
            capacity,
            mode,
            AllowClientChoice: true,
            AssignmentRule.FirstFree,
            services ?? [Cleaning],
            resources ?? [Perez, Mora]);

    private static AvailabilityBlock Create(BlockDefinition definition) =>
        AvailabilityBlock.Create(AvailabilityBlockId.New(), Tenant, LocationId.New(), definition, Now);

    [Fact]
    public void ABlockKeepsItsResourcesInTheOrderTheyWereGiven()
    {
        var block = Create(Weekly(resources: [Mora, Perez]));

        Assert.Equal([Mora, Perez], block.ResourceIds);
        Assert.Equal([Cleaning], block.ServiceIds);
        Assert.Equal(1, block.Version);
    }

    [Fact]
    public void DetachingAResourceRemovesItFromTheBlockAndKeepsTheBlockWhileAnotherRemains()
    {
        var block = Create(Weekly(resources: [Perez, Mora]));

        Assert.True(block.DetachResource(Perez, Now.AddMinutes(1)));

        Assert.Equal([Mora], block.ResourceIds);
        Assert.False(block.Archived);
        Assert.Equal(2, block.Version);
        Assert.False(block.DetachResource(Perez, Now.AddMinutes(2)));
        Assert.Equal(2, block.Version);
    }

    [Fact]
    public void ABlockLeftWithoutResourcesIsArchived()
    {
        var block = Create(Weekly(resources: [Perez]));

        Assert.True(block.DetachResource(Perez, Now.AddMinutes(1)));

        Assert.Empty(block.ResourceIds);
        Assert.True(block.Archived);
    }

    [Fact]
    public void DetachingAServiceRemovesItAndAnEmptiedBlockIsArchived()
    {
        var other = ServiceId.New();
        var block = Create(Weekly(services: [Cleaning, other]));

        Assert.True(block.DetachService(Cleaning, Now));
        Assert.Equal([other], block.ServiceIds);
        Assert.False(block.Archived);

        Assert.True(block.DetachService(other, Now));
        Assert.Empty(block.ServiceIds);
        Assert.True(block.Archived);
        Assert.False(block.DetachService(ServiceId.New(), Now));
    }

    [Fact]
    public void ACapacityOfOneDropsTheSessionModeAndABiggerOneDefaultsToFirstBookingOpens()
    {
        Assert.Null(Create(Weekly(capacity: 1, mode: SessionMode.PublishedSessions)).SessionMode);
        Assert.Equal(SessionMode.FirstBookingOpens, Create(Weekly(capacity: 6)).SessionMode);
        Assert.Equal(SessionMode.PublishedSessions, Create(Weekly(capacity: 6, mode: SessionMode.PublishedSessions)).SessionMode);
    }

    [Fact]
    public void ACapacityBelowOneIsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => Create(Weekly(capacity: 0)));

    [Fact]
    public void ABlockNeedsAtLeastOneServiceAndOneResourceWithoutDuplicates()
    {
        Assert.Throws<ArgumentException>(() => Create(Weekly(services: [])));
        Assert.Throws<ArgumentException>(() => Create(Weekly(resources: [])));
        Assert.Throws<ArgumentException>(() => Create(Weekly(resources: [Perez, Perez])));
        Assert.Throws<ArgumentException>(() => Create(Weekly(services: [Cleaning, Cleaning])));
    }

    [Fact]
    public void TheValidityWindowCannotRunBackwards() =>
        Assert.Throws<ArgumentException>(() => Create(Weekly(validFrom: new(2026, 12, 31), validTo: new(2026, 12, 1))));

    [Fact]
    public void AWeeklyBlockNeedsItsDaysAndAnOneOffBlockExactlyOneDate()
    {
        var withoutDays = Weekly() with { Weekly = [] };
        var oneOffWithoutDate = Weekly() with { Kind = BlockKind.OneOff, Weekly = [], OneOff = null };
        var oneOffWithWeekly = Weekly() with { Kind = BlockKind.OneOff, OneOff = new(new DateOnly(2026, 12, 19), [new TimeRange(480, 960)]) };
        var repeatedDay = Weekly() with { Weekly = [new(1, [new TimeRange(480, 720)]), new(1, [new TimeRange(840, 900)])] };

        Assert.Throws<ArgumentException>(() => Create(withoutDays));
        Assert.Throws<ArgumentException>(() => Create(oneOffWithoutDate));
        Assert.Throws<ArgumentException>(() => Create(oneOffWithWeekly));
        Assert.Throws<ArgumentException>(() => Create(repeatedDay));
    }

    [Fact]
    public void AWeeklyBlockOpensItsRangesOnTheMatchingWeekdayOnly()
    {
        var block = Create(Weekly());
        var monday = new DateOnly(2026, 11, 23);
        var wednesday = new DateOnly(2026, 11, 25);

        Assert.Equal([new TimeRange(480, 720)], block.RangesOn(monday));
        Assert.Equal([new TimeRange(480, 720), new TimeRange(840, 1080)], block.RangesOn(wednesday));
        Assert.Empty(block.RangesOn(new DateOnly(2026, 11, 24)));
    }

    [Fact]
    public void TheValidityWindowIsInclusiveAndOutsideItNothingOpens()
    {
        var block = Create(Weekly(validFrom: new(2026, 12, 1), validTo: new(2026, 12, 7)));

        Assert.Empty(block.RangesOn(new DateOnly(2026, 11, 30)));
        Assert.NotEmpty(block.RangesOn(new DateOnly(2026, 12, 7)));
        Assert.Empty(block.RangesOn(new DateOnly(2026, 12, 14)));
    }

    [Fact]
    public void AnOneOffBlockOpensOnlyOnItsDate()
    {
        var oneOff = Weekly() with
        {
            Kind = BlockKind.OneOff,
            Weekly = [],
            OneOff = new(new DateOnly(2026, 12, 19), [new TimeRange(480, 960)]),
        };
        var block = Create(oneOff);

        Assert.Equal([new TimeRange(480, 960)], block.RangesOn(new DateOnly(2026, 12, 19)));
        Assert.Empty(block.RangesOn(new DateOnly(2026, 12, 20)));
    }

    [Fact]
    public void AnArchivedBlockOpensNothingAndUpdatingBumpsTheVersion()
    {
        var block = Create(Weekly());

        block.Update(Weekly(capacity: 3), Now.AddMinutes(1));
        Assert.Equal((3, 2), (block.CapacityPerResource, (int)block.Version));

        block.Archive(Now.AddMinutes(2));
        Assert.True(block.Archived);
        Assert.Empty(block.RangesOn(new DateOnly(2026, 11, 23)));
    }
}
