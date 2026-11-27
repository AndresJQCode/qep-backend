using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class TaskTypePolicyTests
{
    public static TheoryData<WarehouseTaskType> EveryType() => [.. Enum.GetValues<WarehouseTaskType>()];

    [Fact]
    public void ACountLastsThirtyMinutesHasPriorityThreeAndNoDestinationOverride()
    {
        Assert.Equal(3, TaskTypePolicy.DefaultPriority(WarehouseTaskType.Count));
        Assert.Equal(TimeSpan.FromMinutes(30), TaskTypePolicy.LeaseDuration(WarehouseTaskType.Count));
        Assert.False(TaskTypePolicy.AllowsDestinationOverride(WarehouseTaskType.Count));
    }

    [Fact]
    public void ACountHasNoMovementReason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskTypePolicy.MovementReasonOf(WarehouseTaskType.Count));
    }

    [Fact]
    public void OnlyACountConfirmsByCount()
    {
        Assert.False(TaskTypePolicy.ConfirmsByCount(WarehouseTaskType.Move));
        Assert.True(TaskTypePolicy.ConfirmsByCount(WarehouseTaskType.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskTypePolicy.ConfirmsByCount((WarehouseTaskType)99));
    }

    [Theory]
    [MemberData(nameof(EveryType))]
    public void EveryTypeIsCoveredByEveryPolicy(WarehouseTaskType type)
    {
        _ = TaskTypePolicy.DefaultPriority(type);
        _ = TaskTypePolicy.LeaseDuration(type);
        _ = TaskTypePolicy.AllowsDestinationOverride(type);
        _ = TaskTypePolicy.ConfirmsByCount(type);
        _ = TaskTypePermissions.For(type);
        if (!TaskTypePolicy.ConfirmsByCount(type))
        {
            Assert.IsType<MovementReason>(TaskTypePolicy.MovementReasonOf(type));
        }
    }

    [Fact]
    public void ACountNeedsTheCountPerformPermission()
    {
        Assert.Equal(WmsPermissions.CountPerform, TaskTypePermissions.For(WarehouseTaskType.Count));
    }

    [Fact]
    public void WithOnlyCountPerformTheCallerMayExecuteOnlyCounts()
    {
        var world = new TaskWorld();
        world.Permissions.Clear();
        world.Permissions.Add(WmsPermissions.CountPerform);

        Assert.Equal([WarehouseTaskType.Count], TaskTypePermissions.AllowedFor(world.Context()));
    }
}
