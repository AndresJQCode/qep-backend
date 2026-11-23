using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

/// <summary>
/// Everything the engine needs, read once for a range of days: calendar, rules, blocks, closures, holidays and the resources the caller
/// may offer. The sessions are read apart (<see cref="SessionsAsync"/>) because booking reads them again after it holds the resource lock.
/// Shared by <c>ListAvailability</c>, <c>BookAppointment</c> and <c>RescheduleAppointment</c>, so the three agree on what is a valid start.
/// </summary>
public sealed class AvailabilityLoader(
    ITenantClock tenantClock,
    ISchedulingHolidayCalendar holidays,
    ISchedulingSettingsRepository settings,
    IAvailabilityBlockRepository blocks,
    IClosureRepository closures,
    IResourceRepository resources,
    ISessionRepository sessions)
{
    public async Task<LoadedAvailability> LoadAsync(
        Guid tenantId, ResourceScope scope, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        await LoadAsync(tenantId, scope, await tenantClock.GetAsync(tenantId, cancellationToken), firstDay, lastDay, cancellationToken);

    /// <summary>Loads the tenant's calendar day that holds <paramref name="startsAt"/>: what booking and rescheduling validate a start against.</summary>
    public async Task<LoadedAvailability> LoadForStartAsync(
        Guid tenantId, ResourceScope scope, DateTimeOffset startsAt, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startsAt, calendar.TimeZone).DateTime);
        return await LoadAsync(tenantId, scope, calendar, day, day, cancellationToken);
    }

    /// <summary>The same load for a caller that already read the tenant's calendar (the desk needs "today" before it can pick the day).</summary>
    public async Task<LoadedAvailability> LoadAsync(
        Guid tenantId, ResourceScope scope, TenantCalendar calendar, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        var rules = await settings.FindAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException("The scheduling rules row is missing: first access provisions it before any read.");
        var rangeStart = calendar.StartOfDayUtc(firstDay);
        var rangeEnd = calendar.EndOfDayExclusiveUtc(lastDay);
        var offered = (await resources.ListAsync(tenantId, scope, cancellationToken)).Where(resource => !resource.Archived).ToArray();

        return new LoadedAvailability(
            calendar,
            rules,
            await blocks.ListAsync(tenantId, scope, cancellationToken),
            await closures.ListAsync(tenantId, scope, rangeStart, cancellationToken),
            offered,
            await holidays.HolidaysAsync(tenantId, firstDay, lastDay, cancellationToken),
            firstDay,
            lastDay,
            rangeStart,
            rangeEnd);
    }

    public Task<IReadOnlyList<Session>> SessionsAsync(
        Guid tenantId,
        LoadedAvailability loaded,
        IReadOnlyCollection<ResourceId> resourceIds,
        bool onlyActive,
        CancellationToken cancellationToken) =>
        sessions.ListOverlappingAsync(tenantId, resourceIds, loaded.RangeStart, loaded.RangeEnd, onlyActive, cancellationToken);
}

public sealed record LoadedAvailability(
    TenantCalendar Calendar,
    SchedulingSettings Rules,
    IReadOnlyList<AvailabilityBlock> Blocks,
    IReadOnlyList<Closure> Closures,
    IReadOnlyList<Resource> OfferedResources,
    IReadOnlySet<DateOnly> Holidays,
    DateOnly FirstDay,
    DateOnly LastDay,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd)
{
    public IReadOnlyList<ResourceId> OfferedResourceIds => [.. OfferedResources.Select(resource => resource.Id)];

    public AvailabilityQuery Query(
        Service service, IReadOnlyList<Session> sessions, ResourceId? onlyResource = null, bool ignoreOccupancy = false) =>
        new(
            service,
            Blocks,
            Closures,
            sessions,
            OfferedResources.ToDictionary(resource => resource.Id, resource => resource.LocationId),
            Rules,
            Holidays,
            Calendar.TimeZone,
            FirstDay,
            LastDay,
            Calendar.UtcNow,
            onlyResource,
            ignoreOccupancy);
}
