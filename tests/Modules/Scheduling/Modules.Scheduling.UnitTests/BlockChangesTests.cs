using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

// The audit entry of a block edit lists the fields that changed, not a fixed placeholder.
public sealed class BlockChangesTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly ServiceId Cleaning = ServiceId.New();
    private static readonly ServiceId Whitening = ServiceId.New();
    private static readonly ResourceId Perez = ResourceId.New();
    private static readonly ResourceId Mora = ResourceId.New();

    private static BlockDefinition Definition(
        string name = "Consulta general",
        int capacity = 1,
        int thursdayFrom = 480,
        IReadOnlyList<ServiceId>? services = null,
        IReadOnlyList<ResourceId>? resources = null) =>
        new(
            name,
            BlockKind.Weekly,
            [new WeekdayHours(1, [new TimeRange(480, 720)]), new WeekdayHours(4, [new TimeRange(thursdayFrom, 720)])],
            OneOff: null,
            ValidFrom: null,
            ValidTo: null,
            ClosesOnHolidays: true,
            capacity,
            SessionMode: null,
            AllowClientChoice: true,
            AssignmentRule.FirstFree,
            services ?? [Cleaning],
            resources ?? [Perez, Mora]);

    private static AvailabilityBlock Block() =>
        AvailabilityBlock.Create(AvailabilityBlockId.New(), Tenant, LocationId.New(), Definition(), Now);

    [Fact]
    public void AnEditThatChangesNothingListsNoField() =>
        Assert.Empty(BlockChanges.Between(Block(), Definition()));

    [Fact]
    public void OnlyTheFieldsThatChangedAreListed() =>
        Assert.Equal(["name", "capacityPerResource"], BlockChanges.Between(Block(), Definition(name: "Consulta 2", capacity: 4)));

    [Fact]
    public void AChangeInTheOpeningHoursIsReportedAsTheSchedule() =>
        Assert.Equal(["schedule"], BlockChanges.Between(Block(), Definition(thursdayFrom: 540)));

    [Fact]
    public void ReorderingTheResourcesIsAChangeBecauseTheOrderIsTheAssignmentOrder() =>
        Assert.Equal(["resourceIds"], BlockChanges.Between(Block(), Definition(resources: [Mora, Perez])));

    [Fact]
    public void TheServicesAreASetSoReorderingThemIsNotAChange()
    {
        var block = AvailabilityBlock.Create(
            AvailabilityBlockId.New(), Tenant, LocationId.New(), Definition(services: [Cleaning, Whitening]), Now);

        Assert.Empty(BlockChanges.Between(block, Definition(services: [Whitening, Cleaning])));
        Assert.Equal(["serviceIds"], BlockChanges.Between(block, Definition(services: [Cleaning])));
    }
}
