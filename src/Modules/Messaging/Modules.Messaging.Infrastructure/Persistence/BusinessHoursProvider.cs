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

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return schedule.IsOpenAt(instant, calendar.TimeZone);
    }

    public async Task<BusinessHoursSpan> ElapsedBusinessSecondsAsync(
        Guid tenantId, Guid? scheduleId, DateTimeOffset fromInstant, DateTimeOffset toInstant, CancellationToken cancellationToken)
    {
        var schedule = await ResolveAsync(tenantId, scheduleId, cancellationToken);
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return new BusinessHoursSpan(BusinessHoursClock.BusinessSeconds(schedule, fromInstant, toInstant, calendar.TimeZone), schedule?.Id);
    }

    // The single place that decides which schedule rules: a null or deleted id falls to the default, and no default means none.
    private async Task<BusinessHoursSchedule?> ResolveAsync(Guid tenantId, Guid? scheduleId, CancellationToken cancellationToken)
    {
        if (scheduleId is { } id && await schedules.FindAsync(tenantId, id, cancellationToken) is { } chosen)
        {
            return chosen;
        }

        return await schedules.FindDefaultAsync(tenantId, cancellationToken);
    }
}
