using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Scheduling.Infrastructure.Persistence;

namespace Modules.Scheduling.IntegrationTests;

// Spec 2026-11-21 (Agenda), D-8: la doble reserva la impide la base, no sólo el código. Dos sesiones activas del mismo recurso no
// pueden solaparse en [starts_at, occupies_until); la sesión es única por (recurso, servicio, inicio).
public sealed class SchedulingPersistenceTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 13, 0, 0, TimeSpan.Zero);

    private sealed record Seed(Location Location, Resource Resource, Resource OtherResource, Service Service);

    private static async Task<Seed> SeedAsync(SchedulingDatabase database, int buffer = 0)
    {
        await using var context = database.CreateContext();
        var location = Location.Create(LocationId.New(), Tenant, "Chapinero", true, Now);
        var resource = Resource.Create(ResourceId.New(), Tenant, location.Id, "Dra. Pérez", Guid.CreateVersion7(), Now);
        var other = Resource.Create(ResourceId.New(), Tenant, location.Id, "Dr. Mora", null, Now);
        var service = Service.Create(ServiceId.New(), Tenant, "Limpieza", 30, buffer, true, Now);
        context.AddRange(location, resource, other, service);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new Seed(location, resource, other, service);
    }

    private static Session NewSession(Seed seed, ResourceId resource, DateTimeOffset start, int buffer = 0, int capacity = 1, bool published = false) =>
        Session.Create(SessionId.New(), Tenant, resource, seed.Service.Id, start, start.AddMinutes(30), buffer, capacity, published, Now);

    // Esta base sólo migra la Agenda, pero desde ad8479522 SchedulingUnitOfWork comprueba que el cliente de una cita nueva exista en
    // customers.customers (borrar clientes, spec 2026-11-10, P1/P2). Igual que CustomerReferenceReplica en Quotations: la tabla con las dos
    // columnas que lee esa comprobación y el cliente dado de alta.
    private static async Task<Guid> SeedCustomerAsync(SchedulingDatabase database)
    {
        var customerId = Guid.CreateVersion7();
        await using var context = database.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "CREATE SCHEMA IF NOT EXISTS customers; CREATE TABLE IF NOT EXISTS customers.customers (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);",
            TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO customers.customers (id, tenant_id) VALUES ({customerId}, {Tenant})", TestContext.Current.CancellationToken);
        return customerId;
    }

    private static async Task SaveAsync(SchedulingDatabase database, params Session[] sessions)
    {
        await using var context = database.CreateContext();
        context.Sessions.AddRange(sessions);
        await new SchedulingUnitOfWork(context).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheExclusionConstraintRejectsAnOverlapOfTheSameResource()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        await SaveAsync(database, NewSession(seed, seed.Resource.Id, Start));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            SaveAsync(database, NewSession(seed, seed.Resource.Id, Start.AddMinutes(15))));

        Assert.Equal(SchedulingErrorCodes.SlotTaken, error.Code);
    }

    [Fact]
    public async Task TheConstraintAllowsTheSameRangeOnAnotherResource()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);

        await SaveAsync(database, NewSession(seed, seed.Resource.Id, Start), NewSession(seed, seed.OtherResource.Id, Start));

        await using var context = database.CreateContext();
        Assert.Equal(2, await context.Sessions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnInactiveSessionDoesNotBlockItsRange()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var released = NewSession(seed, seed.Resource.Id, Start);
        released.Reserve(Now);
        released.Release(Now);
        Assert.False(released.IsActive);
        await SaveAsync(database, released);

        await SaveAsync(database, NewSession(seed, seed.Resource.Id, Start.AddMinutes(10)));

        await using var context = database.CreateContext();
        Assert.Equal(2, await context.Sessions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheBufferIsPartOfTheOccupiedRangeAndTouchingRangesDoNotOverlap()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database, buffer: 10);
        await SaveAsync(database, NewSession(seed, seed.Resource.Id, Start, buffer: 10));

        // 08:00–08:30 con margen ocupa hasta 08:40: las 08:30 chocan, las 08:40 sólo tocan el borde.
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            SaveAsync(database, NewSession(seed, seed.Resource.Id, Start.AddMinutes(30), buffer: 10)));
        Assert.Equal(SchedulingErrorCodes.SlotTaken, error.Code);

        await SaveAsync(database, NewSession(seed, seed.Resource.Id, Start.AddMinutes(40), buffer: 10));
    }

    [Fact]
    public async Task TheSameServiceAndStartTwiceIsAlsoTaken()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var first = NewSession(seed, seed.Resource.Id, Start, capacity: 5, published: true);
        await SaveAsync(database, first);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            SaveAsync(database, NewSession(seed, seed.Resource.Id, Start, capacity: 5, published: true)));

        Assert.Equal(SchedulingErrorCodes.SlotTaken, error.Code);
    }

    [Fact]
    public async Task TwoWritersOfTheSameSessionDoNotBothWin()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var session = NewSession(seed, seed.Resource.Id, Start, capacity: 3);
        await SaveAsync(database, session);

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await first.Sessions.SingleAsync(row => row.Id == session.Id, TestContext.Current.CancellationToken);
        var b = await second.Sessions.SingleAsync(row => row.Id == session.Id, TestContext.Current.CancellationToken);
        a.Reserve(Now);
        b.Reserve(Now);

        await new SchedulingUnitOfWork(first).SaveChangesAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            new SchedulingUnitOfWork(second).SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task ABlockRoundTripsItsRangesAndItsOrderedLists()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var definition = new BlockDefinition(
            "Consulta general",
            BlockKind.Weekly,
            [new WeekdayHours(1, [new TimeRange(480, 720)]), new WeekdayHours(3, [new TimeRange(480, 720), new TimeRange(840, 1080)])],
            OneOff: null,
            new DateOnly(2026, 12, 1),
            new DateOnly(2026, 12, 31),
            ClosesOnHolidays: true,
            6,
            SessionMode.PublishedSessions,
            AllowClientChoice: true,
            AssignmentRule.FirstFree,
            [seed.Service.Id],
            [seed.OtherResource.Id, seed.Resource.Id]);
        var block = AvailabilityBlock.Create(AvailabilityBlockId.New(), Tenant, seed.Location.Id, definition, Now);
        await using (var context = database.CreateContext())
        {
            context.Blocks.Add(block);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = database.CreateContext();
        var loaded = await reader.Blocks.SingleAsync(row => row.Id == block.Id, TestContext.Current.CancellationToken);

        Assert.Equal([seed.OtherResource.Id, seed.Resource.Id], loaded.ResourceIds);
        Assert.Equal([seed.Service.Id], loaded.ServiceIds);
        Assert.Equal((6, SessionMode.PublishedSessions), (loaded.CapacityPerResource, loaded.SessionMode));
        Assert.Equal(new DateOnly(2026, 12, 1), loaded.ValidFrom);
        Assert.Equal([new TimeRange(480, 720), new TimeRange(840, 1080)], loaded.RangesOn(new DateOnly(2026, 12, 2)));
        Assert.Equal([new TimeRange(480, 720)], loaded.RangesOn(new DateOnly(2026, 12, 7)));
    }

    [Fact]
    public async Task AnOneOffBlockRoundTripsItsDate()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var definition = new BlockDefinition(
            "Jornada de diciembre", BlockKind.OneOff, [], new DateHours(new DateOnly(2026, 12, 19), [new TimeRange(480, 960)]),
            null, null, false, 1, null, true, AssignmentRule.FirstFree, [seed.Service.Id], [seed.Resource.Id]);
        var block = AvailabilityBlock.Create(AvailabilityBlockId.New(), Tenant, seed.Location.Id, definition, Now);
        await using (var context = database.CreateContext())
        {
            context.Blocks.Add(block);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = database.CreateContext();
        var loaded = await reader.Blocks.SingleAsync(row => row.Id == block.Id, TestContext.Current.CancellationToken);

        Assert.Equal([new TimeRange(480, 960)], loaded.RangesOn(new DateOnly(2026, 12, 19)));
        Assert.Empty(loaded.RangesOn(new DateOnly(2026, 12, 20)));
    }

    [Fact]
    public async Task AnAppointmentRoundTripsWithItsHistory()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var session = NewSession(seed, seed.Resource.Id, Start);
        session.Reserve(Now);
        var actor = Guid.CreateVersion7();
        var customerId = await SeedCustomerAsync(database);
        var appointment = Appointment.Create(
            AppointmentId.New(), Tenant, seed.Location.Id, seed.Resource.Id, seed.Service.Id, customerId, session.Id,
            blockId: null, Start, Start.AddMinutes(30), AppointmentStatus.Pending, AppointmentSource.Channel,
            conversationId: Guid.CreateVersion7(), note: "Primera vez", actor, Now);
        appointment.Confirm(actor, Now.AddMinutes(5));
        await using (var context = database.CreateContext())
        {
            context.Sessions.Add(session);
            context.Appointments.Add(appointment);
            await new SchedulingUnitOfWork(context).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = database.CreateContext();
        var loaded = await reader.Appointments.Include(row => row.Events).SingleAsync(row => row.Id == appointment.Id, TestContext.Current.CancellationToken);

        Assert.Equal((AppointmentStatus.Confirmed, AppointmentSource.Channel, "Primera vez"), (loaded.Status, loaded.Source, loaded.Note));
        Assert.Equal(appointment.ConversationId, loaded.ConversationId);
        Assert.Equal([AppointmentAction.Created, AppointmentAction.Confirmed], loaded.Events.OrderBy(entry => entry.At).Select(entry => entry.Action));
        Assert.Equal(2, loaded.Version);
    }

    [Fact]
    public async Task AClosureAndTheRulesRoundTrip()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var closure = Closure.Create(ClosureId.New(), Tenant, seed.Location.Id, seed.Resource.Id, Start, Start.AddHours(4), "Congreso", Now);
        var rules = SchedulingSettings.Create(Tenant, Now);
        var connection = Guid.CreateVersion7();
        rules.Update(ConfirmationMode.Manual, 60, 90, 12, true, 48, connection, null, Now.AddMinutes(1));
        await using (var context = database.CreateContext())
        {
            context.Closures.Add(closure);
            context.Settings.Add(rules);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = database.CreateContext();
        var loadedClosure = await reader.Closures.SingleAsync(row => row.Id == closure.Id, TestContext.Current.CancellationToken);
        var loadedRules = await reader.Settings.SingleAsync(row => row.TenantId == Tenant, TestContext.Current.CancellationToken);

        Assert.Equal((seed.Resource.Id, "Congreso", Start.AddHours(4)), (loadedClosure.ResourceId, loadedClosure.Reason, loadedClosure.EndsAt));
        Assert.Equal((ConfirmationMode.Manual, 60, 90, 12, true, 48), (loadedRules.ConfirmationMode, loadedRules.MinNoticeMinutes,
            loadedRules.HorizonDays, loadedRules.ClientCancelCutoffHours, loadedRules.ReminderEnabled, loadedRules.ReminderOffsetHours));
        Assert.Equal(connection, loadedRules.ReminderConnectionId);
    }

    [Fact]
    public async Task TheMigrationAppliesFromScratchAndTheModelHasNoPendingChanges()
    {
        await using var database = await SchedulingDatabase.StartAsync();

        await using var context = database.CreateContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(context.Database.HasPendingModelChanges());
        var extensions = await context.Database
            .SqlQueryRaw<string>("SELECT extname AS \"Value\" FROM pg_extension WHERE extname = 'btree_gist'")
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["btree_gist"], extensions);
    }

    // Carrera real sobre (sede, miembro): la verificación previa del caso de uso no la ve y la base sí. Se prueba la traducción directo
    // contra el índice (una concurrencia real sería inestable): sin ella, el índice único respondería 500.
    [Fact]
    public async Task TheMemberLinkUniqueIndexIsTranslatedToItsDomainCode()
    {
        await using var database = await SchedulingDatabase.StartAsync();
        var seed = await SeedAsync(database);
        var member = seed.Resource.MemberId;

        await using var context = database.CreateContext();
        context.Resources.Add(Resource.Create(ResourceId.New(), Tenant, seed.Location.Id, "Dra. Pérez 2", member, Now));

        var exception = await Assert.ThrowsAsync<SchedulingDomainException>(
            () => new SchedulingUnitOfWork(context).SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SchedulingErrorCodes.ResourceMemberAlreadyLinked, exception.Code);
    }
}
