using BuildingBlocks.Application.BusinessHours;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-22 D-14: Messaging owns the business-hours schedules, so it is the one that answers <see cref="IBusinessHoursProvider"/> for
/// the other modules. Which schedule rules is decided once, in <see cref="ResolveAsync"/>: the chosen one if it still exists, otherwise the
/// tenant's default, otherwise none (then the hours are around the clock, the «no schedule = always open» meaning Messaging already has).
/// The zone is the tenant's, from <see cref="ITenantClock"/>, as <c>ListBusinessHoursHandler</c> and <see cref="BusinessHoursRepository"/> do.
/// </summary>
internal sealed class BusinessHoursProvider(IBusinessHoursRepository schedules, ITenantClock tenantClock) : IBusinessHoursProvider
{
    // The provider is scoped, so these live as long as one request or one worker pass over a tenant: a pass that asks about many tickets
    // resolves the schedule and the zone once. A schedule edited during that pass is read with its old shape until the next one.
    private readonly Dictionary<(Guid TenantId, Guid? ScheduleId), BusinessHoursSchedule?> _resolved = [];
    private readonly Dictionary<Guid, TimeZoneInfo> _zones = [];

    public async Task<IReadOnlyList<BusinessHoursScheduleRef>> ListSchedulesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await schedules.ListAsync(tenantId, cancellationToken))
            .Select(schedule => new BusinessHoursScheduleRef(
                schedule.Id, schedule.Name, schedule.IsDefault, schedule.Weekly.Any(day => day.Ranges.Count > 0)))
            .ToArray();

    public async Task<bool> IsOpenAsync(Guid tenantId, Guid? scheduleId, DateTimeOffset instant, CancellationToken cancellationToken)
    {
        var schedule = await ResolveAsync(tenantId, scheduleId, cancellationToken);
        if (schedule is null)
        {
            return true;
        }

        return schedule.IsOpenAt(instant, await ZoneAsync(tenantId, cancellationToken));
    }

    public async Task<BusinessHoursSpan> ElapsedBusinessSecondsAsync(
        Guid tenantId, Guid? scheduleId, DateTimeOffset fromInstant, DateTimeOffset toInstant, CancellationToken cancellationToken)
    {
        var schedule = await ResolveAsync(tenantId, scheduleId, cancellationToken);
        var zone = await ZoneAsync(tenantId, cancellationToken);
        return new BusinessHoursSpan(BusinessHoursClock.BusinessSeconds(schedule, fromInstant, toInstant, zone), schedule?.Id);
    }

    // The single place that decides which schedule rules: a null or deleted id falls to the default, and no default means none.
    private async Task<BusinessHoursSchedule?> ResolveAsync(Guid tenantId, Guid? scheduleId, CancellationToken cancellationToken)
    {
        if (_resolved.TryGetValue((tenantId, scheduleId), out var known))
        {
            return known;
        }

        var schedule = scheduleId is { } id && await schedules.FindAsync(tenantId, id, cancellationToken) is { } chosen
            ? chosen
            : await schedules.FindDefaultAsync(tenantId, cancellationToken);
        _resolved[(tenantId, scheduleId)] = schedule;
        return schedule;
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!_zones.TryGetValue(tenantId, out var zone))
        {
            zone = (await tenantClock.GetAsync(tenantId, cancellationToken)).TimeZone;
            _zones[tenantId] = zone;
        }

        return zone;
    }
}
