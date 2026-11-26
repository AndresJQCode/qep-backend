using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>
/// Spec 2026-11-22 D-14: the tenant's business hours as a shared capability. Which schedule rules is decided once (the chosen one if it
/// exists, else the default, else around the clock) and the hours are counted in the tenant's zone.
/// </summary>
public sealed class BusinessHoursProviderTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // 2026-10-05 is a Monday.
    private static readonly DateTimeOffset MondayNineBogota = new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(-5));

    private static List<WeekdayHours> Week(Func<int, TimeRange[]> ranges) =>
        Enumerable.Range(1, 7).Select(day => new WeekdayHours(day, ranges(day))).ToList();

    // Monday to Friday 08:00-18:00.
    private static BusinessHoursSchedule Office(string name = "Oficina") =>
        BusinessHoursSchedule.Create(
            Guid.CreateVersion7(), TenantId, name, Week(day => day <= 5 ? [new TimeRange(480, 1080)] : []), [], null, Today, Now);

    // Every day 20:00-24:00: never open at nine in the morning.
    private static BusinessHoursSchedule Nights() =>
        BusinessHoursSchedule.Create(
            Guid.CreateVersion7(), TenantId, "Noches", Week(_ => [new TimeRange(1200, 1440)]), [], null, Today, Now);

    // A schedule whose week has no ranges at all: it never opens through its week (spec §5.18).
    private static BusinessHoursSchedule Empty() =>
        BusinessHoursSchedule.Create(Guid.CreateVersion7(), TenantId, "Vacío", Week(_ => []), [], null, Today, Now);

    private static BusinessHoursSchedule AsDefault(BusinessHoursSchedule schedule)
    {
        schedule.MakeDefault(Now);
        return schedule;
    }

    private static BusinessHoursProvider ProviderOf(TimeZoneInfo zone, params BusinessHoursSchedule[] schedules) =>
        new(new FakeSchedules(schedules), new FixedClock(zone));

    [Fact]
    public async Task ElapsedUsesTheChosenSchedule()
    {
        var office = AsDefault(Office());
        var nights = Nights();
        var provider = ProviderOf(Bogota, office, nights);

        var chosen = await provider.ElapsedBusinessSecondsAsync(
            TenantId, nights.Id, MondayNineBogota, MondayNineBogota.AddHours(2), CancellationToken.None);
        var byDefault = await provider.ElapsedBusinessSecondsAsync(
            TenantId, office.Id, MondayNineBogota, MondayNineBogota.AddHours(2), CancellationToken.None);

        Assert.Equal(new BuildingBlocks.Application.BusinessHours.BusinessHoursSpan(0, nights.Id), chosen);
        Assert.Equal(new BuildingBlocks.Application.BusinessHours.BusinessHoursSpan(7200, office.Id), byDefault);
    }

    [Fact]
    public async Task ADeletedScheduleFallsBackToTheDefault()
    {
        var office = AsDefault(Office());
        var provider = ProviderOf(Bogota, office, Nights());

        var span = await provider.ElapsedBusinessSecondsAsync(
            TenantId, Guid.CreateVersion7(), MondayNineBogota, MondayNineBogota.AddHours(2), CancellationToken.None);

        Assert.Equal(office.Id, span.EffectiveScheduleId);
        Assert.Equal(7200, span.BusinessSeconds);
    }

    [Fact]
    public async Task ANullScheduleUsesTheDefault()
    {
        var office = AsDefault(Office());
        var provider = ProviderOf(Bogota, Nights(), office);

        var span = await provider.ElapsedBusinessSecondsAsync(
            TenantId, null, MondayNineBogota, MondayNineBogota.AddHours(2), CancellationToken.None);

        Assert.Equal(office.Id, span.EffectiveScheduleId);
        Assert.Equal(7200, span.BusinessSeconds);
    }

    [Fact]
    public async Task NoScheduleCountsCalendarHours()
    {
        var provider = ProviderOf(Bogota);

        // A Saturday night, which no schedule would count: around the clock counts all of it.
        var from = new DateTimeOffset(2026, 10, 3, 1, 0, 0, TimeSpan.FromHours(-5));
        var span = await provider.ElapsedBusinessSecondsAsync(TenantId, Guid.CreateVersion7(), from, from.AddHours(3), CancellationToken.None);

        Assert.Null(span.EffectiveScheduleId);
        Assert.Equal(3 * 3600, span.BusinessSeconds);
    }

    [Fact]
    public async Task ElapsedUsesTheTenantTimeZone()
    {
        var office = AsDefault(Office());
        // 12:00Z to 20:00Z on a Monday: 07:00-15:00 in Bogota (UTC-5) and 08:00-16:00 in New York (UTC-4 in October).
        var from = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        var inBogota = await ProviderOf(Bogota, office).ElapsedBusinessSecondsAsync(
            TenantId, null, from, from.AddHours(8), CancellationToken.None);
        var inNewYork = await ProviderOf(NewYork, office).ElapsedBusinessSecondsAsync(
            TenantId, null, from, from.AddHours(8), CancellationToken.None);

        Assert.Equal(7 * 3600, inBogota.BusinessSeconds);
        Assert.Equal(8 * 3600, inNewYork.BusinessSeconds);
    }

    [Fact]
    public async Task AScheduleWithoutRangesCountsZero()
    {
        var empty = AsDefault(Empty());
        var provider = ProviderOf(Bogota, empty);

        var span = await provider.ElapsedBusinessSecondsAsync(
            TenantId, null, MondayNineBogota, MondayNineBogota.AddDays(7), CancellationToken.None);

        Assert.Equal(0, span.BusinessSeconds);
        Assert.Equal(empty.Id, span.EffectiveScheduleId);
    }

    [Fact]
    public async Task IsOpenWithoutSchedulesIsTrue()
    {
        var provider = ProviderOf(Bogota);

        Assert.True(await provider.IsOpenAsync(TenantId, null, new DateTimeOffset(2026, 10, 3, 3, 0, 0, TimeSpan.FromHours(-5)), CancellationToken.None));
    }

    [Fact]
    public async Task IsOpenFollowsTheResolvedSchedule()
    {
        var office = AsDefault(Office());
        var provider = ProviderOf(Bogota, office);
        var saturdayNine = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.FromHours(-5));

        Assert.True(await provider.IsOpenAsync(TenantId, null, MondayNineBogota, CancellationToken.None));
        Assert.False(await provider.IsOpenAsync(TenantId, office.Id, saturdayNine, CancellationToken.None));
    }

    [Fact]
    public async Task ListSchedulesReturnsIdNameDefaultAndWeeklyHours()
    {
        var office = AsDefault(Office("Oficina"));
        var empty = Empty();
        var provider = ProviderOf(Bogota, empty, office);

        var list = await provider.ListSchedulesAsync(TenantId, CancellationToken.None);

        Assert.Equal(
            [
                new BuildingBlocks.Application.BusinessHours.BusinessHoursScheduleRef(office.Id, "Oficina", true, true),
                new BuildingBlocks.Application.BusinessHours.BusinessHoursScheduleRef(empty.Id, "Vacío", false, false),
            ],
            list);
    }

    private sealed class FixedClock(TimeZoneInfo zone) : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(Now, zone));
    }

    // Only what the provider reads; the rest of the port is not part of this capability.
    private sealed class FakeSchedules(IReadOnlyList<BusinessHoursSchedule> schedules) : IBusinessHoursRepository
    {
        public Task<BusinessHoursSchedule?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(schedules.FirstOrDefault(schedule => schedule.TenantId == tenantId && schedule.Id == id));

        public Task<IReadOnlyList<BusinessHoursSchedule>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BusinessHoursSchedule>>(
                schedules.Where(schedule => schedule.TenantId == tenantId).OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase).ToArray());

        public Task<BusinessHoursSchedule?> FindDefaultAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(schedules.FirstOrDefault(schedule => schedule.TenantId == tenantId && schedule.IsDefault));

        public Task<bool> NameExistsAsync(Guid tenantId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(BusinessHoursSchedule schedule) => throw new NotSupportedException();

        public void Remove(BusinessHoursSchedule schedule) => throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectionBusinessHours>> ListAssignmentsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddAssignment(ConnectionBusinessHours row) => throw new NotSupportedException();

        public void RemoveAssignment(ConnectionBusinessHours row) => throw new NotSupportedException();

        public Task SwapDefaultAsync(
            BusinessHoursSchedule? previousDefault, BusinessHoursSchedule target, DateTimeOffset now, Guid actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
