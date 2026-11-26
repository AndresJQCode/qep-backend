using Modules.Messaging.Domain;
using Modules.Payroll.Application;

namespace Bootstrapper;

/// <summary>
/// The holidays of Payroll: always the Colombian national ones (<see cref="ColombianHolidays"/> lives in Messaging, hence this adapter
/// in Bootstrapper). A copy of <c>SchedulingHolidays</c> without the country check: payroll is Colombian law whatever the tenant says.
/// </summary>
internal sealed class PayrollHolidayCalendar : IPayrollHolidayCalendar
{
    public Task<IReadOnlySet<DateOnly>> HolidaysAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        Task.FromResult(Between(firstDay, lastDay));

    internal static IReadOnlySet<DateOnly> Between(DateOnly from, DateOnly to)
    {
        var holidays = new HashSet<DateOnly>();
        for (var year = from.Year; year <= to.Year; year++)
        {
            holidays.UnionWith(ColombianHolidays.For(year).Where(day => day >= from && day <= to));
        }

        return holidays;
    }
}
