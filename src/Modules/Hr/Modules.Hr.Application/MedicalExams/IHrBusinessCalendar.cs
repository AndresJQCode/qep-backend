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
}
