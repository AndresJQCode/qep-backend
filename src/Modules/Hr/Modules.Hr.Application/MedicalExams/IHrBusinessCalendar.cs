namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// Business-day arithmetic for the exit exam deadline (spec 2026-11-23 §5.5). Hr cannot reference Support or Messaging, so the
/// adapter lives in the Bootstrapper.
/// </summary>
public interface IHrBusinessCalendar
{
    /// <summary>
    /// The <paramref name="days"/>-th business day strictly after <paramref name="from"/>. Monday to Friday, minus the national
    /// holidays when the tenant regional country is <c>CO</c>.
    /// </summary>
    Task<DateOnly> AddBusinessDaysAsync(Guid tenantId, DateOnly from, int days, CancellationToken cancellationToken);

    /// <summary>
    /// The business days strictly after <paramref name="from"/> up to and including <paramref name="through"/>, by the same calendar as
    /// <see cref="AddBusinessDaysAsync"/>; 0 when <paramref name="through"/> is not after <paramref name="from"/>. It is what
    /// <c>businessDaysLeft</c> counts, so the screen never counts days in the browser.
    /// </summary>
    Task<int> CountBusinessDaysAsync(Guid tenantId, DateOnly from, DateOnly through, CancellationToken cancellationToken);

    /// <summary>
    /// The calendar of the tenant resolved <b>once</b>: counting for every row of a page with <see cref="CountBusinessDaysAsync"/> reads
    /// the regional settings again each time. Same rule, same results.
    /// </summary>
    Task<IHrBusinessDayCounter> CounterAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Counts business days of one tenant without going back to its settings.</summary>
public interface IHrBusinessDayCounter
{
    /// <summary>The business days strictly after <paramref name="from"/> up to and including <paramref name="through"/>.</summary>
    int Count(DateOnly from, DateOnly through);
}
