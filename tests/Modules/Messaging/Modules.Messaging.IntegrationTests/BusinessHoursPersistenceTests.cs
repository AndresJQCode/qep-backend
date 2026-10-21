using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 §5.1 contra Postgres: el jsonb de las franjas ida y vuelta, los dos únicos traducidos por nombre de índice,
/// el <c>RESTRICT</c> de la asignación, el CHECK del país y la resolución asignación → predeterminado → siempre abierto de
/// <c>IBusinessHours</c>.</summary>
public sealed class BusinessHoursPersistenceTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<WeekdayHours> EveryDay(params TimeRange[] ranges) =>
        [.. Enumerable.Range(1, 7).Select(day => new WeekdayHours(day, ranges))];

    private static BusinessHoursSchedule NewSchedule(
        Guid tenantId, string name, IReadOnlyList<WeekdayHours>? weekly = null, IReadOnlyList<DateException>? exceptions = null) =>
        BusinessHoursSchedule.Create(Guid.CreateVersion7(), tenantId, name, weekly ?? EveryDay(), exceptions ?? [], null, Today, Now);

    private static async Task SaveAsync(QepApiFactory factory, params BusinessHoursSchedule[] schedules)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBusinessHoursRepository>();
        foreach (var schedule in schedules)
        {
            repository.Add(schedule);
        }

        await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task WeeklyAndExceptionsRoundTripThroughJsonb()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var schedule = NewSchedule(
            tenantId,
            "Oficina",
            [
                new WeekdayHours(1, [new TimeRange(480, 720), new TimeRange(780, 1020), new TimeRange(1080, 1200)]),
                new WeekdayHours(2, []),
                new WeekdayHours(3, []),
                new WeekdayHours(4, []),
                new WeekdayHours(5, []),
                new WeekdayHours(6, [new TimeRange(540, 780)]),
                new WeekdayHours(7, []),
            ],
            [new DateException(new DateOnly(2026, 11, 2), []), new DateException(new DateOnly(2026, 12, 24), [new TimeRange(480, 720)])]);

        await SaveAsync(factory, schedule);

        using var scope = factory.Services.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IBusinessHoursRepository>().FindAsync(tenantId, schedule.Id, Ct);
        Assert.NotNull(loaded);
        Assert.Equal(schedule.Weekly.Count, loaded.Weekly.Count);
        for (var index = 0; index < schedule.Weekly.Count; index++)
        {
            Assert.Equal(schedule.Weekly[index].Day, loaded.Weekly[index].Day);
            Assert.Equal(schedule.Weekly[index].Ranges, loaded.Weekly[index].Ranges);
        }

        Assert.Equal(schedule.Exceptions.Count, loaded.Exceptions.Count);
        for (var index = 0; index < schedule.Exceptions.Count; index++)
        {
            Assert.Equal(schedule.Exceptions[index].Date, loaded.Exceptions[index].Date);
            Assert.Equal(schedule.Exceptions[index].Ranges, loaded.Exceptions[index].Ranges);
        }

        // La forma guardada es la del spec: claves en minúscula, fechas yyyy-MM-dd.
        Assert.Equal(
            "2026-11-02",
            await ScalarAsync<string>(connectionString, "SELECT exceptions -> 0 ->> 'date' FROM messaging.business_hours_schedules"));
        Assert.Equal(
            "480",
            await ScalarAsync<string>(connectionString, "SELECT weekly -> 0 -> 'ranges' -> 0 ->> 'from' FROM messaging.business_hours_schedules"));
    }

    [Fact]
    public async Task TheNameIndexIgnoresCaseAndIsTranslatedByName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        await SaveAsync(factory, NewSchedule(tenantId, "Oficina"));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => SaveAsync(factory, NewSchedule(tenantId, "OFICINA")));
        await SaveAsync(factory, NewSchedule(Guid.CreateVersion7(), "OFICINA"));

        Assert.Equal(MessagingErrorCodes.BusinessHoursNameTaken, error.Code);
    }

    [Fact]
    public async Task TwoDefaultsAreADefaultConflict()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var first = NewSchedule(tenantId, "Uno");
        var second = NewSchedule(tenantId, "Dos");
        await SaveAsync(factory, first, second);
        await ExecuteAsync(connectionString, "UPDATE messaging.business_hours_schedules SET is_default = true WHERE id = @id", ("id", first.Id));

        // Por SQL: el índice parcial rechaza el segundo predeterminado del tenant.
        var postgres = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString, "UPDATE messaging.business_hours_schedules SET is_default = true WHERE id = @id", ("id", second.Id)));
        Assert.Equal("IX_business_hours_tenant_default", postgres.ConstraintName);

        // Por la unidad de trabajo: la misma violación sale como default_conflict.
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBusinessHoursRepository>();
        var loaded = await repository.FindAsync(tenantId, second.Id, Ct);
        Assert.NotNull(loaded);
        loaded.MakeDefault(Now);
        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal(MessagingErrorCodes.BusinessHoursDefaultConflict, error.Code);
    }

    [Fact]
    public async Task DeletingAnAssignedScheduleIsRestricted()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var schedule = NewSchedule(tenantId, "Oficina");
        await SaveAsync(factory, schedule);
        await ExecuteAsync(
            connectionString,
            "INSERT INTO messaging.connection_business_hours (connection_id, tenant_id, schedule_id, assigned_at) VALUES (@c, @t, @s, now())",
            ("c", Guid.CreateVersion7()), ("t", tenantId), ("s", schedule.Id));

        var postgres = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString, "DELETE FROM messaging.business_hours_schedules WHERE id = @id", ("id", schedule.Id)));

        Assert.Equal("FK_connection_business_hours_schedule", postgres.ConstraintName);
    }

    [Fact]
    public async Task TheHolidayCountryCheckRejectsOtherCountries()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var schedule = NewSchedule(Guid.CreateVersion7(), "Oficina");
        await SaveAsync(factory, schedule);

        var postgres = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString, "UPDATE messaging.business_hours_schedules SET holiday_country = 'US' WHERE id = @id", ("id", schedule.Id)));
        await ExecuteAsync(connectionString, "UPDATE messaging.business_hours_schedules SET holiday_country = 'CO' WHERE id = @id", ("id", schedule.Id));

        Assert.Equal("CK_business_hours_holiday_country", postgres.ConstraintName);
        using var scope = factory.Services.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IBusinessHoursRepository>().FindAsync(schedule.TenantId, schedule.Id, Ct);
        Assert.Equal(HolidayCountry.CO, loaded!.HolidayCountry);
    }

    [Fact]
    public async Task StatusesResolveAssignmentThenDefaultThenAlwaysOpen()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        // 2026-10-21 es miércoles; 12:00 UTC son las 07:00 en Bogotá.
        var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero) };
        using var baseFactory = new QepApiFactory(connectionString);
        using var factory = baseFactory.WithTestClock(clock);
        var tenant = await RegisterTenantAsync(factory);
        var bare = await RegisterTenantAsync(factory);

        var closed = NewSchedule(tenant.TenantId, "Cerrado");
        var open = NewSchedule(tenant.TenantId, "Abierto", EveryDay(new TimeRange(0, 1440)));
        await SaveAsync(baseFactory, closed, open);
        await ExecuteAsync(connectionString, "UPDATE messaging.business_hours_schedules SET is_default = true WHERE id = @id", ("id", open.Id));
        var assigned = Guid.CreateVersion7();
        var unassigned = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            "INSERT INTO messaging.connection_business_hours (connection_id, tenant_id, schedule_id, assigned_at) VALUES (@c, @t, @s, now())",
            ("c", assigned), ("t", tenant.TenantId), ("s", closed.Id));

        using var scope = factory.Services.CreateScope();
        var hours = scope.ServiceProvider.GetRequiredService<IBusinessHours>();
        var statuses = await hours.GetStatusesAsync(tenant.TenantId, [assigned, unassigned], Ct);
        var single = await hours.GetStatusAsync(tenant.TenantId, assigned, Ct);
        var withoutDefault = await hours.GetStatusAsync(bare.TenantId, Guid.CreateVersion7(), Ct);

        Assert.False(statuses[assigned].IsOpen);
        Assert.Equal(closed.Id, statuses[assigned].ScheduleId);
        Assert.Equal("Cerrado", statuses[assigned].ScheduleName);
        Assert.True(statuses[unassigned].IsOpen);
        Assert.Equal(open.Id, statuses[unassigned].ScheduleId);
        Assert.Equal(statuses[assigned], single);
        Assert.True(withoutDefault.IsOpen);
        Assert.Null(withoutDefault.ScheduleId);
        Assert.Null(withoutDefault.NextChangeAt);
    }
}
