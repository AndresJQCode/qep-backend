namespace Modules.Payroll.Application;

/// <summary>
/// Colombian national holidays, always: payroll is Colombian law, so unlike the Agenda the tenant's country is never read. The adapter
/// lives in Bootstrapper because <c>ColombianHolidays</c> lives in Messaging and Payroll does not reference it.
/// </summary>
public interface IPayrollHolidayCalendar
{
    Task<IReadOnlySet<DateOnly>> HolidaysAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}
