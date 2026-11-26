using Microsoft.EntityFrameworkCore;
using Modules.Scheduling.Domain;
using Modules.Scheduling.Infrastructure.Persistence;

namespace Modules.Scheduling.IntegrationTests;

// Spec 2026-11-21 (Agenda), «Aprovisionamiento»: la sede principal y las reglas se crean en el primer acceso, una sola vez por tenant,
// aunque lleguen varias peticiones a la vez. Calco de PipelineProvisioner.
public sealed class SchedulingProvisionerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);

    private static SchedulingProvisioner Provisioner(SchedulingDatabase database) =>
        new(database.CreateContext(), new FixedClock(Now));

    [Fact]
    public async Task TwoConcurrentFirstAccessesCreateOneMainLocation()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var tenant = Guid.CreateVersion7();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            Provisioner(database).EnsureAsync(tenant, $"Clínica {index}", TestContext.Current.CancellationToken)));

        await using var context = database.CreateContext();
        var locations = await context.Locations.Where(location => location.TenantId == tenant).ToListAsync(TestContext.Current.CancellationToken);
        var main = Assert.Single(locations);
        Assert.True(main.IsMain);
        Assert.Equal(1, await context.Settings.CountAsync(settings => settings.TenantId == tenant, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProvisioningTwiceChangesNothing()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var tenant = Guid.CreateVersion7();

        await Provisioner(database).EnsureAsync(tenant, "Clínica Sonrisa", TestContext.Current.CancellationToken);
        await Provisioner(database).EnsureAsync(tenant, "Otro nombre", TestContext.Current.CancellationToken);

        await using var context = database.CreateContext();
        var main = await context.Locations.SingleAsync(location => location.TenantId == tenant, TestContext.Current.CancellationToken);
        Assert.Equal("Clínica Sonrisa", main.Name);
        Assert.Equal(1, main.Version);
        var settings = await context.Settings.SingleAsync(row => row.TenantId == tenant, TestContext.Current.CancellationToken);
        Assert.Equal(1, settings.Version);
    }

    [Fact]
    public async Task EachTenantGetsItsOwnMainLocationAndRules()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await Provisioner(database).EnsureAsync(first, "Uno", TestContext.Current.CancellationToken);
        await Provisioner(database).EnsureAsync(second, "Dos", TestContext.Current.CancellationToken);

        await using var context = database.CreateContext();
        Assert.Equal(["Dos", "Uno"], (await context.Locations.Select(location => location.Name).ToListAsync(TestContext.Current.CancellationToken)).Order());
        Assert.Equal(2, await context.Settings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheProvisionedRulesAreTheDefaults()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var tenant = Guid.CreateVersion7();

        await Provisioner(database).EnsureAsync(tenant, "Clínica", TestContext.Current.CancellationToken);

        await using var context = database.CreateContext();
        var settings = await context.Settings.SingleAsync(row => row.TenantId == tenant, TestContext.Current.CancellationToken);
        Assert.Equal(
            (SchedulingDefaults.ConfirmationMode, SchedulingDefaults.MinNoticeMinutes, SchedulingDefaults.HorizonDays,
                SchedulingDefaults.ClientCancelCutoffHours, SchedulingDefaults.ReminderEnabled, SchedulingDefaults.ReminderOffsetHours),
            (settings.ConfirmationMode, settings.MinNoticeMinutes, settings.HorizonDays, settings.ClientCancelCutoffHours,
                settings.ReminderEnabled, settings.ReminderOffsetHours));
        Assert.Equal((Now, Now), (settings.CreatedAt, settings.UpdatedAt));
    }

    [Fact]
    public async Task AnArchivedOrSecondMainLocationCannotBeInsertedDirectly()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var tenant = Guid.CreateVersion7();
        await Provisioner(database).EnsureAsync(tenant, "Clínica", TestContext.Current.CancellationToken);

        await using var context = database.CreateContext();
        context.Locations.Add(Location.Create(LocationId.New(), tenant, "Otra principal", isMain: true, Now));

        // El índice único parcial es la garantía: aunque un camino se salte al aprovisionador, la base no deja dos principales.
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FixedClock(DateTimeOffset now) : BuildingBlocks.Application.IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
