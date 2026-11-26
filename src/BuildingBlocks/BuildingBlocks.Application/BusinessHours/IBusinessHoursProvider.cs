namespace BuildingBlocks.Application.BusinessHours;

/// <summary>A business-hours schedule of a tenant, by name: what a screen needs to offer it as a choice.</summary>
/// <param name="HasWeeklyHours">false if no day of the week has ranges (that schedule never opens through its week).</param>
public sealed record BusinessHoursScheduleRef(Guid Id, string Name, bool IsDefault, bool HasWeeklyHours);

/// <summary>How much of an interval fell inside the tenant's business hours, and under which schedule it was counted.</summary>
/// <param name="EffectiveScheduleId">The schedule used; null = the tenant has none and the interval was counted around the clock.</param>
public sealed record BusinessHoursSpan(long BusinessSeconds, Guid? EffectiveScheduleId);

/// <summary>
/// Spec 2026-11-22 D-14: the tenant's business hours as a capability shared between modules, the same pattern as
/// <c>IRealtimePublisher</c> and <c>IUserReferenceProbe</c>: the contract lives in BuildingBlocks and the module that owns the data
/// (Messaging) implements and registers it, so a consumer such as Support needs no reference to Messaging and no adapter of its own.
/// The provider does not look at whether the <c>messaging</c> module is on: the schedules are data of the tenant and stay when it is off.
/// </summary>
public interface IBusinessHoursProvider
{
    /// <summary>The tenant's schedules, by name.</summary>
    Task<IReadOnlyList<BusinessHoursScheduleRef>> ListSchedulesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>scheduleId null or nonexistent → the tenant's default; no schedules at all → always open.</summary>
    Task<bool> IsOpenAsync(Guid tenantId, Guid? scheduleId, DateTimeOffset instant, CancellationToken cancellationToken);

    /// <summary>The same resolution as <see cref="IsOpenAsync"/>; with no schedules, wall-clock seconds. The bounds are named
    /// <c>fromInstant</c> and <c>toInstant</c> because CA1716 reserves <c>to</c> (a Visual Basic keyword) as an interface parameter name.</summary>
    Task<BusinessHoursSpan> ElapsedBusinessSecondsAsync(
        Guid tenantId, Guid? scheduleId, DateTimeOffset fromInstant, DateTimeOffset toInstant, CancellationToken cancellationToken);
}
