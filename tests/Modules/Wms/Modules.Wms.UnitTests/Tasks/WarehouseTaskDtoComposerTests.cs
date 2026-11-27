using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Tasks;

public sealed class WarehouseTaskDtoComposerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDetailResolvesCodesNamesAndQuantities()
    {
        var world = new TaskWorld();
        var task = world.AddTask(assignTo: TaskWorld.Member);

        var dto = await world.Composer().ComposeAsync(task, null, withEvents: false, Ct);

        Assert.Equal(("Move", "Assigned", 3, 3), (dto.Type, dto.Status, dto.Priority, dto.EffectivePriority));
        Assert.Equal("Ana Operaria", dto.AssignedMemberName);
        Assert.Equal((1, 1), (dto.LineCount, dto.OpenLineCount));
        Assert.Equal("A-01-01", dto.FirstFromLocationCode);
        var line = Assert.Single(dto.Lines);
        Assert.Equal(("V1", "Café", "UND"), (line.VariantCode, line.ProductName, line.BaseUom));
        Assert.Equal(("A-01-01", "A-01-02"), (line.FromLocationCode, line.ToLocationCode));
        Assert.Equal((5m, 0m, 5m), (line.QuantityPlanned, line.QuantityConfirmed, line.QuantityRemaining));
        Assert.Equal(("Open", "Available"), (line.LineStatus, line.StockStatus));
        Assert.Empty(dto.Events);
    }

    // M4: la composición corre con la tarea y los saldos bloqueados: sólo lee las ubicaciones de sus líneas, sin la bodega entera.
    [Fact]
    public async Task TheDetailReadsOnlyTheLocationsOfItsLines()
    {
        var world = new TaskWorld();
        var task = world.AddTask();

        var dto = await world.Composer().ComposeAsync(task, null, withEvents: false, Ct);

        Assert.Equal(("A-01-01", "A-01-02"), (dto.Lines[0].FromLocationCode, dto.Lines[0].ToLocationCode));
        // LocationId no es comparable: se compara como conjunto.
        var read = Assert.Single(world.LocationReads);
        Assert.Equal(2, read.Count);
        Assert.True(new HashSet<LocationId> { world.From, world.To }.SetEquals(read));
        Assert.DoesNotContain("warehouse-tree", world.Log);
    }

    [Fact]
    public async Task SummariesReadOnlyTheFirstSourceOfEachTask()
    {
        var world = new TaskWorld();
        var task = world.AddTask(lines: [world.Line(), WarehouseTaskLine.Create(
            TaskWorld.Variant, null, StockStatus.Available, world.Spare, world.To, 1)]);

        var dto = Assert.Single(await world.Composer().ComposeSummariesAsync(TaskWorld.Tenant, [task], Ct));

        Assert.Equal("A-01-01", dto.FirstFromLocationCode);
        Assert.Equal([world.From], Assert.Single(world.LocationReads).ToArray());
        Assert.DoesNotContain("warehouse-tree", world.Log);
    }

    [Fact]
    public async Task OnlyTheClaimerReceivesTheLeaseToken()
    {
        var world = new TaskWorld();
        var task = world.AddTask();
        task.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start);

        var mine = await world.Composer().ComposeAsync(task, TaskWorld.Member, withEvents: false, Ct);
        var other = await world.Composer().ComposeAsync(task, TaskWorld.OtherMember, withEvents: false, Ct);
        var nobody = await world.Composer().ComposeAsync(task, null, withEvents: false, Ct);

        Assert.Equal(task.LeaseToken, mine.LeaseToken);
        Assert.Null(other.LeaseToken);
        Assert.Null(nobody.LeaseToken);
        Assert.All([mine, other, nobody], dto =>
        {
            Assert.Equal(TaskWorld.Member, dto.ClaimedByMemberId);
            Assert.Equal("Ana Operaria", dto.ClaimedByMemberName);
            Assert.Equal(task.LeaseExpiresAt, dto.LeaseExpiresAt);
        });
    }

    // El barrido conserva en la fila quién reclamó y su token para la confirmación tardía: nada de eso se muestra.
    [Fact]
    public async Task AnExpiredClaimIsNotShownToAnyone()
    {
        var world = new TaskWorld();
        var task = world.AddTask();
        task.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start);
        task.ExpireLease(TaskWorld.Start.AddMinutes(16));

        var dto = await world.Composer().ComposeAsync(task, TaskWorld.Member, withEvents: false, Ct);

        Assert.NotNull(task.LeaseToken);
        Assert.Null(dto.LeaseToken);
        Assert.Null(dto.LeaseExpiresAt);
        Assert.Null(dto.ClaimedByMemberId);
        Assert.Null(dto.ClaimedByMemberName);
        var summary = Assert.Single(await world.Composer().ComposeSummariesAsync(TaskWorld.Tenant, [task], Ct));
        Assert.Null(summary.ClaimedByMemberId);
    }

    // Los hechos de una transacción comparten fecha: se ordenan por fecha y, dentro de ella, en orden causal (el del enum).
    [Fact]
    public async Task TheEventsComeInCausalOrderWithTheirMemberNames()
    {
        var world = new TaskWorld();
        var task = world.AddTask();
        task.Claim(TaskWorld.Member, TaskWorld.Device, TaskWorld.Start.AddMinutes(1));
        task.ConfirmLine(
            TaskWorld.Member, task.LeaseToken!.Value, TaskWorld.Device, task.Lines[0].Id, 5, TaskWorld.Start.AddMinutes(2));

        var dto = await world.Composer().ComposeAsync(task, null, withEvents: true, Ct);

        Assert.Equal(
            ["Created", "Published", "Claimed", "LineConfirmed", "Completed"], dto.Events.Select(value => value.Kind));
        Assert.Equal("Sin nombre", dto.Events[0].MemberName);
        Assert.Equal("Ana Operaria", dto.Events[2].MemberName);
        Assert.Equal(5m, dto.Events[3].Quantity);
    }

    [Fact]
    public async Task UrgencyShowsInTheEffectivePriority()
    {
        var world = new TaskWorld();
        var task = world.AddTask();
        task.ChangePriority(2, true, TaskWorld.OtherMember, TaskWorld.Start);
        world.Now = TaskWorld.Start.AddMinutes(TaskPriority.AgingStepMinutes);

        var dto = Assert.Single(await world.Composer().ComposeSummariesAsync(TaskWorld.Tenant, [task], Ct));

        Assert.Equal(2 + TaskPriority.UrgentBonus + 1, dto.EffectivePriority);
        Assert.Null(dto.AssignedMemberName);
    }
}
