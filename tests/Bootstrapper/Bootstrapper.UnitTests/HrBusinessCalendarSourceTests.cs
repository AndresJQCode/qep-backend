using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-23 (medical exams) Â§5.5: the exit exam is due on the 5th business day strictly after the termination. Monday to
/// Friday, minus the national holidays when the tenant country is Colombia (same rule as <c>SupportBusinessCalendar</c>).
/// </summary>
public sealed class HrBusinessCalendarSourceTests
{
    // 2026-11-16 is a Colombian holiday Monday.
    private static readonly DateOnly Friday = new(2026, 11, 13);

    [Fact]
    public async Task AddBusinessDaysSkipsWeekendsAndColombianHolidays()
    {
        var tenant = Guid.CreateVersion7();
        var colombia = new HrBusinessCalendarSource(new Directory("CO"));
        var unitedStates = new HrBusinessCalendarSource(new Directory("US"));

        Assert.Equal(new DateOnly(2026, 11, 17), await colombia.AddBusinessDaysAsync(tenant, Friday, 1, CancellationToken.None));
        Assert.Equal(new DateOnly(2026, 11, 16), await unitedStates.AddBusinessDaysAsync(tenant, Friday, 1, CancellationToken.None));
    }

    [Fact]
    public async Task AddBusinessDaysFiveDaysCountsStrictlyAfterTheStart()
    {
        var calendar = new HrBusinessCalendarSource(new Directory("US"));

        Assert.Equal(
            new DateOnly(2026, 11, 20),
            await calendar.AddBusinessDaysAsync(Guid.CreateVersion7(), Friday, 5, CancellationToken.None));
    }

    [Fact]
    public async Task CountBusinessDaysCountsAfterTheStartUpToAndIncludingTheEnd()
    {
        var tenant = Guid.CreateVersion7();
        var colombia = new HrBusinessCalendarSource(new Directory("CO"));
        var unitedStates = new HrBusinessCalendarSource(new Directory("US"));
        var monday = new DateOnly(2026, 11, 23);

        // Friday 11-13 to Tuesday 11-17: Monday 11-16 is a Colombian holiday, so Colombia counts only the Tuesday.
        Assert.Equal(1, await colombia.CountBusinessDaysAsync(tenant, Friday, new DateOnly(2026, 11, 17), CancellationToken.None));
        Assert.Equal(2, await unitedStates.CountBusinessDaysAsync(tenant, Friday, new DateOnly(2026, 11, 17), CancellationToken.None));
        // The same day, and an end before the start, have nothing left.
        Assert.Equal(0, await colombia.CountBusinessDaysAsync(tenant, monday, monday, CancellationToken.None));
        Assert.Equal(0, await colombia.CountBusinessDaysAsync(tenant, monday, Friday, CancellationToken.None));
        // Round trip with AddBusinessDays.
        var due = await colombia.AddBusinessDaysAsync(tenant, Friday, 5, CancellationToken.None);
        Assert.Equal(5, await colombia.CountBusinessDaysAsync(tenant, Friday, due, CancellationToken.None));
    }

    // One lookup of the regional settings for any number of counts: what the overview does for every ExitPending row of a page.
    [Fact]
    public async Task CounterResolvesTheCalendarOnceAndCountsLikeCountBusinessDays()
    {
        var directory = new Directory("CO");
        var source = new HrBusinessCalendarSource(directory);
        var tenant = Guid.CreateVersion7();

        var counter = await source.CounterAsync(tenant, CancellationToken.None);

        Assert.Equal(1, counter.Count(Friday, new DateOnly(2026, 11, 17)));
        Assert.Equal(0, counter.Count(Friday, Friday));
        var far = counter.Count(Friday, new DateOnly(2026, 11, 27));
        Assert.Equal(1, directory.Lookups);
        Assert.Equal(await source.CountBusinessDaysAsync(tenant, Friday, new DateOnly(2026, 11, 27), CancellationToken.None), far);
    }

    [Fact]
    public async Task AddBusinessDaysFailsForATenantWithoutRegionalSettings()
    {
        var calendar = new HrBusinessCalendarSource(new Directory(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            calendar.AddBusinessDaysAsync(Guid.CreateVersion7(), Friday, 5, CancellationToken.None));
    }

    private sealed class Directory(string? country) : ITenantDirectory
    {
        public int Lookups { get; private set; }

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult<TenantRegionalSettings?>(country is null
                ? null
                : new TenantRegionalSettings("America/Bogota", "dd/MM/yyyy", "COP", "es-CO", country));
        }

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
